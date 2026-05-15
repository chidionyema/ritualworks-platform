using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.Search.Application;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration.ReadFrom.Configuration(context.Configuration).WriteTo.Console().Enrich.FromLogContext();
});

builder.AddServiceDefaults();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// CDC via Kafka (Debezium) — enabled when ConnectionStrings:kafka is configured.
// When Kafka is not available, CDC events flow through MassTransit/RabbitMQ
// via ProductCacheInvalidatedEvent and CategoryUpdatedEvent published by Catalog.
var kafkaConn = builder.Configuration.GetConnectionString("kafka");
if (!builder.Environment.IsEnvironment("Test") && !string.IsNullOrEmpty(kafkaConn))
{
    builder.AddKafkaConsumer<string, string>("kafka", consumerBuilder =>
    {
        consumerBuilder.Config.GroupId = "search-svc-cdc";
        consumerBuilder.Config.AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest;
    });
    builder.Services.AddCdcSearchIndexing();
}

builder.Services.AddPlatformAuthentication(builder.Configuration);

builder.Services.AddControllers();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// One-shot bootstrap of the Elasticsearch index settings. Wrapped in
// try/catch + warning so a transiently down Elasticsearch on first deploy
// doesn't crash app boot — both apps come up alongside each other on Fly.
using (var scope = app.Services.CreateScope())
{
    var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");
    try
    {
        await index.EnsureSettingsAsync();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Elasticsearch settings bootstrap failed; will retry on next cold start");
    }
}

app.Run();

public partial class Program { }
