using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Payments.Api.Webhooks;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);

// Bind webhook options. DataAnnotations validation is OFF here because the
// integration tests stub Stripe's WebhookSecret per-test rather than via
// IOptions<>; production gets the real value from Vault. The controller
// guards with an explicit IsNullOrEmpty check.
builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName));

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
app.MapControllers();

app.Run();

// Top-level Program needs partial declaration so WebApplicationFactory<Program>
// in the integration test project can find it.
public partial class Program { }
