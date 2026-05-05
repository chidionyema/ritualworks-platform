using Haworks.BuildingBlocks.Extensions;
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
        "If-Match")
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
    builder.Services.AddHttpClient(name, client =>
    {
        client.BaseAddress = new Uri($"https+http://{name}");
    });
}

// T2.3: dedicated typed client for the circuit-breaker demo. Same target
// as Catalog (resolved by Aspire service-discovery) but registered under a
// separate name so the demo's policy doesn't affect real catalog traffic.
// The Polly circuit breaker itself lives statically in DemoController so
// it survives across requests (per-session state would defeat the point).
builder.Services.AddHttpClient(BackendClients.CatalogDemo, client =>
{
    client.BaseAddress = new Uri($"https+http://{BackendClients.Catalog}");
    client.Timeout = TimeSpan.FromSeconds(3);
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

        // T2.5: closes the persisted -> consumed loop for the event-flow demo.
        // Subscribes to DemoOutboxEvent (relayed from payments-svc's outbox)
        // and emits OnEventFlow stage='consumed' to the SignalR hub.
        mt.AddConsumer<DemoOutboxEventConsumer>();

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
// CORS must run before auth so the preflight OPTIONS request is answered
// without challenging credentials. Demo endpoints are AllowAnonymous so
// position relative to UseAuthentication doesn't matter for the response,
// but it does matter for the OPTIONS preflight.
app.UseCors("portfolio-site");
// Activity middleware sits before auth so a 401 still records traffic into
// the IngressEvents24h counter. Path-scoped to /api/demo/* internally so
// non-demo routes have zero overhead.
app.UseDemoActivityCounters();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<CheckoutHub>("/hubs/checkout");
app.MapHub<DemoHub>("/hubs/demo");

app.Run();

public partial class Program { }
