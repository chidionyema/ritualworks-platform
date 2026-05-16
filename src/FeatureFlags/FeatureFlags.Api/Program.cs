using Haworks.BuildingBlocks.Messaging;
using Haworks.FeatureFlags.Api.Application;
using Haworks.FeatureFlags.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using Haworks.BuildingBlocks.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("featureflags")
    ?? throw new InvalidOperationException("ConnectionStrings:featureflags is required.");

builder.Services.AddDbContext<FeatureFlagsDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "featureflags"));
});

// Redis distributed cache — used by StackExchange.Redis for IDistributedCache consumers.
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("redis")
        ?? throw new InvalidOperationException("ConnectionStrings:redis is required.");
});

builder.Services.AddSingleton<IFeatureFlagCache, FeatureFlagCache>();

builder.Services.AddPlatformAuthentication(builder.Configuration);

builder.Services.AddMassTransit(mt =>
{
    mt.AddEntityFrameworkOutbox<FeatureFlagsDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    mt.AddConsumer<FeatureFlagUpdatedConsumer>();

    if (!builder.Environment.IsEnvironment("Test"))
    {
        mt.UsingRabbitMq((context, cfg) =>
        {
            var rabbitConn = builder.Configuration.GetConnectionString("rabbitmq")
                ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is required.");

            cfg.Host(new Uri(rabbitConn));
            cfg.ConfigureStandardRabbitMq(context);
        });
    }
    else
    {
        mt.UsingInMemory((context, cfg) =>
        {
            cfg.ConfigureEndpoints(context);
        });
    }
});

builder.Services.AddHealthChecks().AddDbHealthCheck<FeatureFlagsDbContext>();
builder.Services.AddApplication();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Cache warmup on startup ensures sub-millisecond evaluation from the very first request.
using (var scope = app.Services.CreateScope())
{
    try
    {
        var cache = scope.ServiceProvider.GetRequiredService<IFeatureFlagCache>();
        await cache.WarmupAsync(default);
    }
    catch (Exception ex)
    {
        // Fail-open: log warning but don't prevent startup if DB is unreachable
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Failed to warmup feature flag cache. Service will start but evaluations may default to false.");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }
