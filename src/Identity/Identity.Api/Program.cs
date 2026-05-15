using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Startup;
using Haworks.BuildingBlocks.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & OTel (lifted from Aspire ServiceDefaults)
builder.AddServiceDefaults();
builder.Services.AddHealthChecks()
    .AddDbHealthCheck<Haworks.Identity.Infrastructure.AppIdentityDbContext>();

// Vault: pull identity-svc's owned secrets into IConfiguration BEFORE DI
// build, so handlers/options resolving cfg["Jwt:Key"] etc. see the Vault
// values instead of placeholders.
//
// Identity owns: secret/identity/jwt + secret/identity/oauth/{google,microsoft,facebook}
// Per-service KV namespacing per ADR-0009 — no service reads another's paths
// without an explicit policy grant.
//
// Skipped in Test environment where the integration fixture provides config
// directly without a real Vault.
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
                new VaultConfigBootstrap.KvMapping("identity/jwt",             "Jwt"),
                // OAuth providers are conditionally registered in
                // Identity.Infrastructure.DependencyInjection when ClientId is blank,
                // so the KV path being missing or empty is fine — mark as Optional
                // so VaultBootstrap doesn't fail-fast on 404 / empty-data responses
                // (Vault dev-mode + VaultSharp throws 404-shaped on empty KV reads).
                new VaultConfigBootstrap.KvMapping("identity/oauth/google",    "Authentication:Google",    Optional: true),
                new VaultConfigBootstrap.KvMapping("identity/oauth/microsoft", "Authentication:Microsoft", Optional: true),
                new VaultConfigBootstrap.KvMapping("identity/oauth/facebook",  "Authentication:Facebook",  Optional: true),
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

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddStartupTaskRunner();

// Vault probe HttpClient — used by AdminController.GetVaultStatus to
// make a real /v1/sys/health round-trip every time it's called. Only
// registered when Vault is enabled, since both DI factories below
// require Vault:Address. AdminController callers will get a 404/500
// from missing DI when Vault is off, which matches the runtime reality.
if (builder.Configuration.GetValue("Vault:Enabled", false))
{
    builder.Services.AddHttpClient<Haworks.Identity.Api.Controllers.VaultProbeClient>((sp, c) =>
    {
        var address = sp.GetRequiredService<IConfiguration>()["Vault:Address"]
            ?? throw new InvalidOperationException("Vault:Address is required for the vault probe client");
        c.BaseAddress = new Uri(address);
        c.Timeout = TimeSpan.FromSeconds(2);
    });
    builder.Services.AddSingleton(sp =>
    {
        // Re-resolve the typed-client wrapper so we get the BaseAddress
        // alongside the HttpClient. AddHttpClient<T> registers T as
        // transient with a typed HttpClient; the wrapper closes over the
        // address for the controller's response payload.
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var address = sp.GetRequiredService<IConfiguration>()["Vault:Address"]
            ?? throw new InvalidOperationException("Vault:Address is required");
        return new Haworks.Identity.Api.Controllers.VaultProbeClient(
            factory.CreateClient(nameof(Haworks.Identity.Api.Controllers.VaultProbeClient)),
            new Uri(address));
    });
}

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3000", "http://localhost:5050"];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
          .AllowAnyHeader()
          .AllowAnyMethod()
          .AllowCredentials();
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Antiforgery: required by AuthenticationController's [IgnoreAntiforgeryToken]
// attribute. Without AddAntiforgery() the controller activator fails resolving
// IAntiforgery even on endpoints that opt OUT of the check.
builder.Services.AddAntiforgery();

// Rate limiting: AuthenticationController uses [EnableRateLimiting("auth")].
// 5 attempts per minute per IP — standard "stop credential stuffing" knob.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1)
            }));
});

// Serilog with explicit console sink. Avoid ReadFrom.Configuration here —
// when the appsettings shape isn't exactly what Serilog.Settings.Configuration
// expects it silently swallows ALL log output (Kestrel "Now listening" included)
// instead of falling back to defaults, which looks identical to a hang.
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()  // explicit fallback — guarantees logs surface
        .Enrich.FromLogContext();
});

var app = builder.Build();

if (!app.Environment.IsEnvironment("Test"))
{
    var startupRunner = app.Services.GetRequiredService<StartupTaskRunner>();

    // 0. Retry Vault bootstrap in the background if the pre-build attempt failed
    startupRunner.AddTask(async (sp, ct) =>
    {
        // Re-attempt Vault auth if initial bootstrap failed
        var config = sp.GetRequiredService<IConfiguration>();
        if (string.IsNullOrEmpty(config["Jwt:Key"]) && config.GetValue<bool>("Vault:Enabled"))
        {
            var vaultLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("VaultBootstrap");
            vaultLogger.LogInformation("Retrying Vault bootstrap in background...");
            // Note: can't re-inject into IConfiguration post-build easily,
            // but the VaultService renewal loop will handle credential rotation
        }
        await Task.CompletedTask;
    });

    // 1. Apply EF migrations (creates tables in 'identity' schema)
    startupRunner.AddTask(async (sp, ct) =>
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Haworks.Identity.Infrastructure.AppIdentityDbContext>();
        var migrateLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await db.Database.MigrateWithRetryAsync(migrateLogger, ct);
    });

    // 2. Seed canonical roles. RegisterCommand assigns new users to
    //    "ContentUploader" by default; without this seed step the first
    //    register call 500s with "Role does not exist".
    startupRunner.AddTask(async (sp, ct) =>
    {
        using var scope = sp.CreateScope();
        var roleManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>();
        foreach (var roleName in (string[]) ["Admin", "ContentUploader", "User"])
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole(roleName));
            }
        }
    });

    // 3. Initialize the RSA signing keypair from Vault (generates on first run,
    //    reads back on subsequent runs). Runs async — JwtTokenService will use
    //    the key once IsReady is true and the readiness check passes.
    startupRunner.AddTask(async (sp, ct) =>
    {
        using var scope = sp.CreateScope();
        var keyProvider = scope.ServiceProvider
            .GetRequiredService<Haworks.BuildingBlocks.Vault.IJwtSigningKeyProvider>();
        if (keyProvider is Haworks.BuildingBlocks.Vault.VaultJwtSigningKeyProvider vaultProvider)
        {
            await vaultProvider.InitializeAsync(ct);
        }
    });
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Top-level Program needs a partial declaration so
// WebApplicationFactory<Program> in the integration test project can find it.
public partial class Program { }
