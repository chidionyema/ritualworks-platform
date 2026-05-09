using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Vault;
using Haworks.Notifications.Application;
using Haworks.Notifications.Infrastructure;
using Haworks.Notifications.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

if (builder.Configuration.GetValue("Vault:Enabled", false)
    && !builder.Environment.IsEnvironment("Test"))
{
    var bootstrapLogger = LoggerFactory
        .Create(b => b.AddConsole())
        .CreateLogger("VaultBootstrap");

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

builder.Services.AddNotificationsInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddNotificationsApplication(builder.Configuration);
builder.Services.AddPostgresIdempotency<NotificationsDbContext>();

builder.Services.AddJwksAuthentication(builder.Configuration);
builder.Services.AddHttpContextAccessor();

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
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider
        .GetRequiredService<NotificationsDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await db.Database.MigrateWithRetryAsync(logger);
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseIdempotency();
app.MapControllers();

app.Run();

public partial class Program { }
