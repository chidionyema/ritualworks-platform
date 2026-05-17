using Haworks.BuildingBlocks.Behaviors;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Persistence;
using Haworks.RulesEngine.Api.Domain;
using Haworks.RulesEngine.Api.Infrastructure;
using MassTransit;
using MediatR;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .Enrich.FromLogContext());

// ── Persistence ───────────────────────────────────────────────────────────────
builder.Services.AddDbContext<RulesDbContext>(options =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("rules")
            ?? builder.Configuration.GetConnectionString("rulesdb")
            ?? throw new InvalidOperationException("ConnectionStrings:rules is missing."),
        npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "rules"));
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbHealthCheck<RulesDbContext>("rules-db");

// ── Auth ─────────────────────────────────────────────────────────────────────
builder.Services.AddPlatformAuthentication(builder.Configuration);

// ── Application ───────────────────────────────────────────────────────────────
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);
builder.Services.AddScoped<IRulesEvaluator, RulesEvaluator>();

// ── MassTransit (fraud check consumer) ───────────────────────────────────────
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddMassTransit(mt =>
    {
        mt.SetKebabCaseEndpointNameFormatter();
        mt.AddConsumer<FraudCheckConsumer>();
        mt.AddConsumer<GlobalFaultConsumer>();

        mt.AddEntityFrameworkOutbox<RulesDbContext>(o =>
        {
            o.UsePostgres();
            o.UseBusOutbox();
            o.QueryDelay = TimeSpan.FromSeconds(1);
            o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
        });

        mt.UsingRabbitMq((context, cfg) =>
        {
            var rabbitConn = builder.Configuration.GetConnectionString("rabbitmq")
                ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is missing.");
            cfg.Host(new Uri(rabbitConn));
            cfg.ConfigureEndpoints(context);
        });
    });
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Migrate + seed fraud rules on startup ─────────────────────────────────────
if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RulesDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<RulesDbContext>>();
    await db.Database.MigrateWithRetryAsync(logger);

    // Seed fraud rules if none exist
    if (!await db.Set<Rule>().AnyAsync(r => r.Name.StartsWith("fraud:")))
    {
        var fraudRules = new[]
        {
            new Rule { Id = Guid.NewGuid(), Name = "fraud:high-value-guest", Expression = "totalAmount > 500 AND isGuest == true", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Rule { Id = Guid.NewGuid(), Name = "fraud:excessive-amount", Expression = "totalAmount > 5000", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Rule { Id = Guid.NewGuid(), Name = "fraud:bulk-items", Expression = "itemCount > 20", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Rule { Id = Guid.NewGuid(), Name = "fraud:high-risk-country", Expression = "countryCode == \"XX\" OR countryCode == \"YY\"", IsActive = true, CreatedAt = DateTime.UtcNow },
        };
        db.Set<Rule>().AddRange(fraudRules);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Count} fraud detection rules", fraudRules.Length);
    }
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }
