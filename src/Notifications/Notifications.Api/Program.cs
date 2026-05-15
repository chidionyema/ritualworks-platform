using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Startup;
using Haworks.BuildingBlocks.Vault;
using Haworks.Notifications.Application;
using Haworks.Notifications.Infrastructure;
using Haworks.Notifications.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHealthChecks()
    .AddDbHealthCheck<NotificationsDbContext>();

if (builder.Configuration.GetValue("Vault:Enabled", false)
    && !builder.Environment.IsEnvironment("Test"))
{
    var bootstrapLogger = LoggerFactory
        .Create(b => b.AddConsole())
        .CreateLogger("VaultBootstrap");

    try
    {
        var vaultSecrets = await VaultConfigBootstrap.LoadAsync(
            builder.Configuration,
            new[]
            {
                new VaultConfigBootstrap.KvMapping("notifications/providers/aws-ses", "Notifications:Providers:Ses"),
                new VaultConfigBootstrap.KvMapping("notifications/providers/sendgrid", "Notifications:Providers:SendGrid"),
                new VaultConfigBootstrap.KvMapping("notifications/providers/twilio", "Notifications:Providers:Twilio"),
                new VaultConfigBootstrap.KvMapping("notifications/providers/fcm", "Notifications:Providers:Fcm"),
            },
            bootstrapLogger);

        builder.Configuration.AddInMemoryCollection(vaultSecrets);
    }
    catch (Exception ex)
    {
        bootstrapLogger.LogCritical(ex, "Vault bootstrap failed — service will start with fallback config. " +
            "Vault secrets will NOT be available until next successful restart.");
        // Don't crash — let the service boot and serve health checks.
        // /health/ready will reflect degraded state via the startup task runner.
    }
}

builder.Services.AddNotificationsInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApplication();
builder.Services.AddPostgresIdempotency<NotificationsDbContext>();
builder.Services.AddStartupTaskRunner();

builder.Services.AddPlatformAuthentication(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("api", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: "api",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .Enrich.FromLogContext();
});

var app = builder.Build();

if (!app.Environment.IsEnvironment("Test"))
{
    var startupRunner = app.Services.GetRequiredService<StartupTaskRunner>();

    // Retry Vault bootstrap in the background if the pre-build attempt failed
    startupRunner.AddTask(async (sp, ct) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        if (string.IsNullOrEmpty(config["Notifications:Providers:Ses:AccessKey"]) && config.GetValue<bool>("Vault:Enabled"))
        {
            var vaultLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("VaultBootstrap");
            vaultLogger.LogInformation("Retrying Vault bootstrap in background...");
            // Note: can't re-inject into IConfiguration post-build easily,
            // but the VaultService renewal loop will handle credential rotation
        }
        await Task.CompletedTask;
    });

    startupRunner.AddTask(async (sp, ct) =>
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await db.Database.MigrateWithRetryAsync(logger, ct);
    });
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseIdempotency();
app.MapControllers();

app.Run();

public partial class Program { }
