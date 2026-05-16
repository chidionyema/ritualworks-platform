using Haworks.BuildingBlocks.Behaviors;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Persistence;
using Haworks.RulesEngine.Api.Domain;
using Haworks.RulesEngine.Api.Infrastructure;
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

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Migrate on startup ────────────────────────────────────────────────────────
if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RulesDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<RulesDbContext>>();
    await db.Database.MigrateWithRetryAsync(logger);
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
