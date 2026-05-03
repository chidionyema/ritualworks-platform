using Haworks.BuildingBlocks.Extensions;
using Haworks.BffWeb.Api;
using Haworks.BffWeb.Api.SignalR;
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
app.UseAuthorization();
app.MapControllers();
app.MapHub<CheckoutHub>("/hubs/checkout");

app.Run();

public partial class Program { }
