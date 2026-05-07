using Haworks.BuildingBlocks.Extensions;
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

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);

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
builder.Services.AddSingleton<IDemoTraceStore, DemoTraceStore>();
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

// CORS for the portfolio site dev server (http://localhost:4321 by default).
// AllowCredentials is required for SignalR's negotiate handshake. Header
// + method allowlists match what the demos actually send.
builder.Services.AddCors(o => o.AddPolicy("portfolio-site", p => p
    .WithOrigins("http://localhost:4321")
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
    BackendClients.Payments, BackendClients.Checkout,
})
{
    var serviceName = name; // capture for handlers
    builder.Services.AddHttpClient(name, client =>
    {
        client.BaseAddress = new Uri($"https+http://{name}");
    })
    // Chaos fault injection runs FIRST: while "paused" via the topology
    // map, the request short-circuits to a synthetic 503 before any
    // network call. Order matters — must compose before the instance-id
    // capture handler so an injected response doesn't pollute the
    // upstream-hop list.
    .AddHttpMessageHandler(sp => new ChaosFaultInjectionHandler(
        sp.GetService<ChaosManager>(),
        serviceName))
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
builder.Services.AddHttpClient(BackendClients.CatalogDemo, client =>
{
    client.BaseAddress = new Uri($"https+http://{BackendClients.Catalog}");
    client.Timeout = TimeSpan.FromSeconds(3);
})
.AddHttpMessageHandler(sp => new ChaosFaultInjectionHandler(
    sp.GetService<ChaosManager>(),
    BackendClients.Catalog))
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
            cfg.ConfigureEndpoints(context);
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
