using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddPostgresIdempotency<OrderDbContext>();

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

var app = builder.Build();

// Auto-apply EF migrations at startup. orders-svc owns the 'orders' schema
// in its own database. Skipped in Test env where the integration fixture
// applies migrations explicitly.
if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider
        .GetRequiredService<Haworks.Orders.Infrastructure.OrderDbContext>();
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
// Idempotency middleware sits AFTER auth (so ICurrentUserService.UserId is
// populated and the key gets user-scoped) and BEFORE the controller pipeline
// so duplicate POSTs return 409 without invoking the handler. Routes that
// don't send X-Idempotency-Key pass through untouched.
app.UseIdempotency();
app.MapControllers();

app.Run();

public partial class Program { }
