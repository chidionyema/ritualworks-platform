using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & OTel (lifted from Aspire ServiceDefaults)
builder.AddServiceDefaults();

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

    var vaultSecrets = await VaultConfigBootstrap.LoadAsync(
        builder.Configuration,
        new[]
        {
            new VaultConfigBootstrap.KvMapping("identity/jwt",             "Jwt"),
            new VaultConfigBootstrap.KvMapping("identity/oauth/google",    "Authentication:Google"),
            new VaultConfigBootstrap.KvMapping("identity/oauth/microsoft", "Authentication:Microsoft"),
            new VaultConfigBootstrap.KvMapping("identity/oauth/facebook",  "Authentication:Facebook"),
        },
        bootstrapLogger);

    builder.Configuration.AddInMemoryCollection(vaultSecrets);
}

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);

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

// Auto-apply EF migrations on startup. Identity-svc owns its DB schema, so
// it's the only thing that should be writing DDL. In a polyrepo world this
// also means: deploying identity-svc is the ONLY way the identity DB schema
// changes — no shared migration runner, no other service touching its tables.
//
// In production this should be gated behind an opt-in flag and run via a
// separate Job container instead of inline at API startup. For dev + portfolio
// this is fast and obvious.
if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();

    // 1. Apply EF migrations (creates tables in 'identity' schema)
    var db = scope.ServiceProvider
        .GetRequiredService<Haworks.Identity.Infrastructure.AppIdentityDbContext>();
    var migrateLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await db.Database.MigrateWithRetryAsync(migrateLogger);

    // 2. Seed canonical roles. RegisterCommand assigns new users to
    //    "ContentUploader" by default; without this seed step the first
    //    register call 500s with "Role does not exist".
    var roleManager = scope.ServiceProvider
        .GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>();
    foreach (var roleName in new[] { "Admin", "ContentUploader", "User" })
    {
        if (!await roleManager.RoleExistsAsync(roleName))
        {
            await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole(roleName));
        }
    }

    // 3. Initialize the RSA signing keypair from Vault (generates on first run,
    //    reads back on subsequent runs). Must complete before request handling
    //    so JwtTokenService has a SigningKey to use.
    var keyProvider = scope.ServiceProvider
        .GetRequiredService<Haworks.BuildingBlocks.Vault.IJwtSigningKeyProvider>();
    if (keyProvider is Haworks.BuildingBlocks.Vault.VaultJwtSigningKeyProvider vaultProvider)
    {
        await vaultProvider.InitializeAsync();
    }
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
app.UseRateLimiter();
app.MapControllers();

app.Run();

// Top-level Program needs a partial declaration so
// WebApplicationFactory<Program> in the integration test project can find it.
public partial class Program { }
