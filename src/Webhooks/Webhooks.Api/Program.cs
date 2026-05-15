using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.Webhooks.Api.Controllers;
using Haworks.Webhooks.Application;
using Haworks.Webhooks.Infrastructure;
using Haworks.Webhooks.Infrastructure.Messaging;
using Haworks.Webhooks.Infrastructure.Workers;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Haworks.Webhooks.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.AddServiceDefaults();

builder.Services.AddApplication();
builder.Services.AddWebhooksInfrastructure(builder.Configuration, builder.Environment);

// Kafka Consumer for CDC (Industry Standard) - Webhook Fan-out
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.AddKafkaConsumer<string, string>("kafka", consumerBuilder =>
    {
        consumerBuilder.Config.GroupId = "webhooks-svc-cdc";
        consumerBuilder.Config.AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest;
    });
    builder.Services.AddHostedService<CdcFanOutWorker>();
}

// MassTransit for Domain Events
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<EventFanOutConsumer>();
    
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration.GetConnectionString("rabbitmq"));
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks()
    .AddDbHealthCheck<Haworks.Webhooks.Infrastructure.Persistence.WebhooksDbContext>();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>();
    await db.Database.MigrateAsync();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }
