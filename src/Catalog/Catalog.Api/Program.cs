using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.BuildingBlocks.Middleware;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddPostgresIdempotency<CatalogDbContext>();

builder.Services.AddPlatformAuthentication(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// T2.6: HybridCache for the cache-stampede demo. Default in-memory L1 only —
// production should add a distributed L2 (Redis) via .AddDistributedCache().
builder.Services.AddHybridCache();

// Serilog with explicit Console fallback (per docs/runbooks/serilog-silent-swallow.md).
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .Enrich.FromLogContext();
});

var app = builder.Build();

// Auto-apply EF migrations at startup. Catalog-svc owns the 'catalog' schema
// in its own database. In prod this should be a separate Job container; for
// dev + portfolio this is the obvious place.
if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider
        .GetRequiredService<Haworks.Catalog.Infrastructure.CatalogDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await db.Database.MigrateWithRetryAsync(logger);
}

app.MapDefaultEndpoints();

// Stamp X-Instance-Id on every response so the caller (BFF, portfolio-site)
// can show which catalog replica handled the request. Active under
// WithReplicas(N) in the AppHost; harmless under N=1 (constant id).
app.UseInstanceIdHeader();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
// Idempotency middleware — opt-in via X-Idempotency-Key header. Sits
// AFTER auth so the stored key is server-side scoped to UserId.
app.UseIdempotency();
app.MapControllers();

app.Run();

// Top-level Program needs partial declaration so WebApplicationFactory<Program>
// in the integration test project can find it.
public partial class Program { }
