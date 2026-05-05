using Haworks.BuildingBlocks.Extensions;
using Haworks.BffWeb.Api;
using Haworks.BffWeb.Api.Demo;
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

// MassTransit + the bff-web-side SignalR-bridge consumer. Production
// transport is RabbitMQ; the integration fixture grafts an in-memory
// harness with this same consumer wired in.
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddMassTransit(mt =>
    {
        mt.SetKebabCaseEndpointNameFormatter();
        mt.AddConsumer<PaymentSessionCreatedConsumer>();
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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<CheckoutHub>("/hubs/checkout");
app.MapHub<DemoHub>("/hubs/demo");

app.Run();

public partial class Program { }
