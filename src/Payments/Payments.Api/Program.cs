using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Vault;
using Haworks.Payments.Api.Webhooks;
using Haworks.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHealthChecks()
    .AddDbHealthCheck<PaymentDbContext>();

// Vault: pull payments-svc's owned secrets (Stripe + PayPal) into
// IConfiguration BEFORE DI build, so the existing IOptions<PaymentProviderOptions>
// binding in Payments.Infrastructure picks them up without code changes.
//
// Per-service KV namespacing per ADR-0009 — payments-svc's AppRole policy
// only grants secret/data/payments/* read.
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
            // KV keys (SecretKey, WebhookSecret, ...) become config keys
            // PaymentProviders:Stripe:SecretKey via the ConfigPrefix, lining
            // up exactly with PaymentProviderOptions binding (Stripe nested
            // under PaymentProviders).
            new VaultConfigBootstrap.KvMapping("payments/stripe", "PaymentProviders:Stripe"),
            new VaultConfigBootstrap.KvMapping("payments/paypal", "PaymentProviders:PayPal"),
        },
        bootstrapLogger);

    builder.Configuration.AddInMemoryCollection(vaultSecrets);
}

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApplication();
builder.Services.AddPostgresIdempotency<PaymentDbContext>();

builder.Services.AddPlatformAuthentication(builder.Configuration);

// Bind webhook options. DataAnnotations validation is OFF here because the
// integration tests stub Stripe's WebhookSecret per-test rather than via
// IOptions<>; production gets the real value from Vault. The controller
// guards with an explicit IsNullOrEmpty check.
builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Serilog with explicit Console fallback (per docs/runbooks/serilog-silent-swallow.md).
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .Enrich.FromLogContext();
});

var app = builder.Build();

// Auto-apply EF migrations at startup. payments-svc owns the 'payments'
// schema in its own database. Skipped in Test env where the integration
// fixture applies migrations explicitly.
if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider
        .GetRequiredService<Haworks.Payments.Infrastructure.PaymentDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await db.Database.MigrateWithRetryAsync(logger);
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Enable request-body buffering on webhook endpoints so the controller can
// read the raw bytes (for signature verification) without breaking the
// MVC model binder downstream. Scoped to /webhooks/* only — global buffering
// would defeat streaming responses elsewhere.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/webhooks"))
    {
        context.Request.EnableBuffering();
    }
    await next();
});

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
// Idempotency middleware — opt-in via X-Idempotency-Key. Server-side
// scoped by UserId so cross-user replay is impossible.
app.UseIdempotency();
app.MapControllers();

app.Run();

// Top-level Program needs partial declaration so WebApplicationFactory<Program>
// in the integration test project can find it.
public partial class Program { }
