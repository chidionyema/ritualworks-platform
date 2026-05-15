using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.BuildingBlocks.Persistence;
using Haworks.CheckoutOrchestrator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddPostgresIdempotency<CheckoutDbContext>();

builder.Services.AddPlatformAuthentication(builder.Configuration);

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

builder.Services.AddHealthChecks()
    .AddDbHealthCheck<Haworks.CheckoutOrchestrator.Infrastructure.Persistence.CheckoutDbContext>();

var app = builder.Build();

if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider
        .GetRequiredService<Haworks.CheckoutOrchestrator.Infrastructure.CheckoutDbContext>();
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
// Idempotency middleware — opt-in via X-Idempotency-Key. Server-side
// scoped by UserId; checkout's POST /api/checkouts is the highest-value
// caller since the BFF retries on transient failures.
app.UseIdempotency();
app.MapControllers();

app.Run();

public partial class Program { }
