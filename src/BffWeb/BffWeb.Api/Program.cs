using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Middleware;
using Haworks.BffWeb.Api;
using Haworks.BffWeb.Api.Demo;
using Haworks.BffWeb.Api.Middleware;
using Haworks.BffWeb.Api.SignalR;
using Haworks.BffWeb.Application.Interfaces;
using MassTransit;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Phase A: JWT validation + user-id propagation. Every backend service
// validates the bearer token (AddPlatformAuthentication), and the BFF
// forwards the user identity as X-User-Id (UserIdentityForwardingHandler).
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddTransient<Haworks.BuildingBlocks.Authentication.UserIdentityForwardingHandler>(sp => new Haworks.BuildingBlocks.Authentication.UserIdentityForwardingHandler(sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(), sp.GetService<Haworks.BuildingBlocks.Authentication.IServiceTokenProvider>()));

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApplication(builder.Configuration);

// CDC via Kafka (Debezium) — enabled when ConnectionStrings:kafka is configured.
// When Kafka is not available, CDC events flow through MassTransit/RabbitMQ
// via ProductCacheInvalidatedEvent and CategoryUpdatedEvent published by Catalog.
var kafkaConn = builder.Configuration.GetConnectionString("kafka");
if (!builder.Environment.IsEnvironment("Test") && !string.IsNullOrEmpty(kafkaConn))
{
    builder.AddKafkaConsumer<string, string>("kafka", consumerBuilder =>
    {
        consumerBuilder.Config.GroupId = "bff-web-cdc";
        consumerBuilder.Config.AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest;
    });
    builder.Services.AddHostedService<Haworks.BffWeb.Application.Consumers.BffCdcCacheInvalidator>();
}

// ── Rate Limiting ──────────────────────────────────────────────────
// Three tiers: global per-IP, authenticated per-user, and strict
// per-IP for expensive operations (saga start, event trigger).
// Configurable via RateLimiting section in appsettings / env vars.
var rlSection = builder.Configuration.GetSection("RateLimiting");
var globalPermits = rlSection.GetValue("GlobalPermitsPerMinute", 120);
var userPermits = rlSection.GetValue("UserPermitsPerMinute", 60);
var expensivePermits = rlSection.GetValue("ExpensivePermitsPerMinute", 10);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global: per-IP sliding window — catches scrapers and unauthenticated abuse
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter
        .Create<HttpContext, string>(ctx =>
            System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
                {
                    PermitLimit = globalPermits,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 4,
                }));

    // "authenticated": per-user token bucket — tighter than IP for logged-in users
    options.AddPolicy("authenticated", ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: ctx.User?.FindFirst("sub")?.Value
                ?? ctx.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
            {
                TokenLimit = userPermits,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                TokensPerPeriod = userPermits,
            }));

    // "expensive": per-IP fixed window — saga start, event trigger, vault rotate
    options.AddPolicy("expensive", ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = expensivePermits,
                Window = TimeSpan.FromMinutes(1),
            }));

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Task.CompletedTask;
    };
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

// Demo surface for the portfolio site (https://github.com/chidionyema/portfolio-site).
// SignalRDemoHubNotifier MUST be Singleton — registering Scoped triggers a
// captive-dependency crash at boot when ValidateScopes is on. The notifier
// only depends on IHubContext<DemoHub> + ILogger so Singleton is safe.
// State store + trace store are also Singleton (in-process, lifetime of
// the AppHost). See src/BffWeb/BffWeb.Api/Demo/ for impls.
builder.Services.AddSingleton<IDemoHubNotifier, SignalRDemoHubNotifier>();
// IDemoTraceStore + DemoTraceStore registration removed alongside the
// hardcoded tracing demo. Real OTel via Tempo will replace this path.
builder.Services.AddSingleton<DemoStateStore>();

// T2.1: replaced Phase 1's hardcoded SystemController literals with real
// probes. ActivityCounters tracks live request counts + p99 latency from
// the rolling histogram fed by DemoActivityMiddleware. HealthProbe pings
// each downstream microservice + RabbitMQ in parallel with a 2s per-target
// timeout. See src/BffWeb/BffWeb.Api/Demo/{DemoActivityCounters, DependencyHealthProbe}.cs.
builder.Services.AddSingleton<IDemoActivityCounters, DemoActivityCounters>();
builder.Services.AddScoped<IDependencyHealthProbe, DependencyHealthProbe>();

// Live console broadcaster — Singleton ring buffer + SignalR fan-out for the
// visitor-facing activity dock. See LiveConsoleBroadcaster + LiveConsoleHub.
builder.Services.AddSingleton<LiveConsoleBroadcaster>();

// Journey scheduler — BackgroundService that fires a rotating canonical
// journey through the cluster every ~20s (place-order saga, idempotent
// retry burst, OCC race). Drives the always-on "watch the system work"
// view on the portfolio site without requiring a visitor to click anything.
// Runs in every environment, including production.
builder.Services.AddHostedService<JourneyScheduler>();

// Upstream warmup — fires GET /health against every backend microservice
// once after the BFF binds, in parallel, with a small retry budget so any
// sleeping Fly machine has time to autostart. Eliminates the cold-start
// race that used to leave the BFF's resilience-handler circuit breakers
// tripped open after the first burst of demo traffic.
builder.Services.AddHostedService<UpstreamWarmup>();

// Resilience tuning for the portfolio cluster. The default
// AddStandardResilienceHandler from ServiceDefaults trips its circuit
// breaker on 10% failures over a 30s window — too eager for the demo's
// low-traffic profile, where a single cold-start storm easily passes
// the threshold and then the breaker stays open because half-open
// probes are sparse. Override:
//   • FailureRatio       0.10 → 0.70   (only trip on sustained failures)
//   • MinimumThroughput  100  → 10     (fits demo traffic)
//   • SamplingDuration   30s  → 15s    (failures decay fast)
//   • BreakDuration      5s   → 3s     (auto-recover quickly)
// Real downstream-protection still applies: a backend that's genuinely
// dead (>70% errors over 10 requests in 15s) still trips the breaker;
// it just doesn't stay open for ages on a single transient blip.
builder.Services.Configure<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>(o =>
{
    o.CircuitBreaker.FailureRatio       = 0.70;
    o.CircuitBreaker.MinimumThroughput  = 10;
    o.CircuitBreaker.SamplingDuration   = TimeSpan.FromSeconds(15);
    o.CircuitBreaker.BreakDuration      = TimeSpan.FromSeconds(3);
});

// HttpContext access for the upstream-capture handler. Required so the
// per-call DelegatingHandler can append its hop to the live HTTP request's
// items dictionary (no AsyncLocal indirection needed).
builder.Services.AddHttpContextAccessor();

// Chaos manager — only in dev. Pauses .NET service processes (kill -STOP)
// or docker containers (docker pause) so a visitor can take a node down
// from the topology map and watch other demos route around it.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<ChaosManager>();
}

// CORS for the portfolio site. Origins read from Cors:AllowedOrigins
// (Fly secret on prod, appsettings on dev). Defaults cover the Astro dev
// server + the canonical Cloudflare Pages URL so a fresh prod deploy
// works without an extra config step. Add a custom domain by setting
// Cors__AllowedOrigins__N=https://your-domain on the BFF Fly app.
//
// AllowCredentials is required for SignalR's negotiate handshake. Header
// + method allowlists match what the demos actually send.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? (builder.Environment.IsDevelopment()
        ? new[] { "http://localhost:4321", "https://haworks.pages.dev", "https://portfolio-showcase.pages.dev" }
        : new[] { "https://haworks.pages.dev", "https://portfolio-showcase.pages.dev" });

builder.Services.AddCors(o => o.AddPolicy("portfolio-site", p => p
    .WithOrigins(corsOrigins)
    .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
    .WithHeaders(
        "Content-Type", "Authorization", "X-Correlation-ID",
        "X-Requested-With", "Accept", "Origin",
        "X-Demo-Session", "X-Idempotency-Key", "X-Idempotency-Ttl-Seconds",
        "If-Match",
        // SignalR's JS client adds this on the negotiate POST. Without it
        // in the allowlist, the browser blocks the preflight and no demo
        // can subscribe to push events.
        "x-signalr-user-agent")
    .WithExposedHeaders("X-Trace-Id", "X-Correlation-ID")
    .AllowCredentials()));

// Typed HttpClient keys for service composition. BaseAddress uses Aspire's
// service-discovery URI form `https+http://<svc>` (configured by
// AddServiceDiscovery() inside ServiceDefaults). Picks https when the target
// service offers it, falls back to http.
foreach (var name in new[]
{
    BackendClients.Identity, BackendClients.Catalog, BackendClients.Orders,
    BackendClients.Payments, BackendClients.Checkout, BackendClients.Search,
    BackendClients.Location, BackendClients.Webhooks, BackendClients.Payouts,
    BackendClients.Scheduler, BackendClients.Privacy, BackendClients.Merchant,
    BackendClients.Notifications, BackendClients.Content, BackendClients.Audit,
})
{
    var serviceName = name; // capture for handlers
    builder.Services.AddHttpClient(name, (sp, client) =>
    {
        client.BaseAddress = new Uri($"https+http://{name}");
        var t = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Haworks.BuildingBlocks.Resilience.HttpClientTimeoutOptions>>().Value;
        client.Timeout = TimeSpan.FromSeconds(t.BffBackendSeconds);
    })
    // Chaos fault injection runs FIRST: while "paused" via the topology
    // map, the request short-circuits to a synthetic 503 before any
    // network call. Order matters — must compose before the instance-id
    // capture handler so an injected response doesn't pollute the
    // upstream-hop list.
    .AddHttpMessageHandler(sp => new ChaosFaultInjectionHandler(
        sp.GetService<ChaosManager>(),
        serviceName))
    .AddHttpMessageHandler(sp => new Haworks.BuildingBlocks.Authentication.UserIdentityForwardingHandler(sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(), sp.GetService<Haworks.BuildingBlocks.Authentication.IServiceTokenProvider>()))
    // Record the upstream replica's X-Instance-Id into the live-console
    // hop list. Service name is closed over so the handler knows which
    // backend the call is targeting (the resolved URI host loses that
    // friendly name post Aspire service-discovery).
    .AddHttpMessageHandler(sp => new UpstreamInstanceCaptureHandler(
        sp.GetRequiredService<IHttpContextAccessor>(),
        serviceName));
}

// T2.3: dedicated typed client for the circuit-breaker demo. Same target
// as Catalog (resolved by Aspire service-discovery) but registered under a
// separate name so the demo's policy doesn't affect real catalog traffic.
// The Polly circuit breaker itself lives statically in DemoController so
// it survives across requests (per-session state would defeat the point).
//
// Bare 3s timeout, NO standard resilience: ServiceDefaults wires
// AddStandardResilienceHandler globally, which retries 5xx responses.
// That defeats the demo — we WANT 503s to bubble up instantly so the
// outer Polly circuit can count them. AddStandardResilienceHandler()
// here a second time replaces the global registration with a no-op
// configuration just for this client.
builder.Services.AddHttpClient(BackendClients.CatalogDemo, (sp, client) =>
{
    client.BaseAddress = new Uri($"https+http://{BackendClients.Catalog}");
    var t = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Haworks.BuildingBlocks.Resilience.HttpClientTimeoutOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(t.BffCatalogDemoSeconds);
})
.AddHttpMessageHandler(sp => new ChaosFaultInjectionHandler(
    sp.GetService<ChaosManager>(),
    BackendClients.Catalog))
.AddHttpMessageHandler(sp => new Haworks.BuildingBlocks.Authentication.UserIdentityForwardingHandler(sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(), sp.GetService<Haworks.BuildingBlocks.Authentication.IServiceTokenProvider>()))
.AddHttpMessageHandler(sp => new UpstreamInstanceCaptureHandler(
    sp.GetRequiredService<IHttpContextAccessor>(),
    BackendClients.Catalog))
.AddStandardResilienceHandler(o =>
{
    // Outer demo Polly does retry/breaker; this handler must be a no-op for
    // failures so 503s bubble up instantly. MaxRetryAttempts=0 fails the
    // Range(1,...) validator at startup, so we keep the default count and
    // make the predicate never match.
    o.Retry.ShouldHandle = static _ => ValueTask.FromResult(false);
    o.CircuitBreaker.ShouldHandle = static _ => ValueTask.FromResult(false);
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(3);
});

// MassTransit + the bff-web-side SignalR-bridge consumer. Production
// transport is RabbitMQ; the integration fixture grafts an in-memory
// harness with this same consumer wired in.
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddMassTransit(mt =>
    {
        mt.SetKebabCaseEndpointNameFormatter();
        mt.AddConsumer<PaymentSessionCreatedConsumer>();

        // T2.2 commit 2: bridge each saga state-change event to a SignalR
        // OnSagaStep push so the portfolio's CheckoutDemo updates in real
        // time as the saga progresses. One consumer per event; each
        // translates to a step + progress percent matching the frontend's
        // stage ladder. See SagaStepBridgeConsumers.cs.
        mt.AddConsumer<StockReservedSagaBridge>();
        mt.AddConsumer<StockReservationFailedSagaBridge>();
        mt.AddConsumer<PaymentSessionCreatedSagaBridge>();
        mt.AddConsumer<PaymentSessionFailedSagaBridge>();
        mt.AddConsumer<PaymentCompletedSagaBridge>();
            mt.AddConsumer<PaymentAmountMismatchSagaBridge>();
            mt.AddConsumer<VaultRotationStageBridge>();

        // T2.5: closes the persisted -> consumed loop for the event-flow demo.
        // Subscribes to DemoOutboxEvent (relayed from payments-svc's outbox)
        // and emits OnEventFlow stage='consumed' to the SignalR hub.
        mt.AddConsumer<DemoOutboxEventConsumer>();

        // #1+#2: bridges catalog-svc ProductCacheInvalidatedEvent (PUT/DELETE
        // on /api/demo/cache/product/*) to a SignalR OnCacheEvent push so
        // connected portfolio clients see real cache invalidations as they
        // happen.
        mt.AddConsumer<ProductCacheInvalidatedBridge>();

        mt.UsingRabbitMq((context, cfg) =>
        {
            var rabbitConn = builder.Configuration.GetConnectionString("rabbitmq")
                ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is missing.");
            cfg.Host(new Uri(rabbitConn));
            cfg.ConfigureStandardRabbitMq(context);
        });
    });
}

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .Enrich.FromLogContext();
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Stamp X-Instance-Id on every response. BFF stays at one replica today
// (the portfolio-site frontend hardcodes :5050 and the SignalR sticky-
// routing story under WithReplicas needs its own validation), but the
// header lets every demo show "this BFF instance" alongside whichever
// upstream replica it called.
app.UseInstanceIdHeader();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Skip HTTPS redirect in dev — portfolio-site hits http://localhost:5050
// and a 307→https cross-scheme redirect blocks the browser fetch.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
// CORS must run before auth so the preflight OPTIONS request is answered
// without challenging credentials. Demo endpoints are AllowAnonymous so
// position relative to UseAuthentication doesn't matter for the response,
// but it does matter for the OPTIONS preflight.
app.UseCors("portfolio-site");
app.UseRateLimiter();
// Activity middleware sits before auth so a 401 still records traffic into
// the IngressEvents24h counter. Path-scoped to /api/demo/* internally so
// non-demo routes have zero overhead.
app.UseDemoActivityCounters();
// Live console: emit a structured event per /api/* request so the
// portfolio's bottom-right dock can render real activity. Must run after
// CORS + activity counters so the dock only sees requests the browser
// was allowed to send and we don't double-instrument latency.
app.UseLiveConsole();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<CheckoutHub>("/hubs/checkout");
app.MapHub<DemoHub>("/hubs/demo");
app.MapHub<LiveConsoleHub>("/hubs/console");

app.Run();

public partial class Program { }
