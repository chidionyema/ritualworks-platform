using Haworks.Audit.Application;
using Haworks.Audit.Application.Capture;
using Haworks.Audit.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Audit's Postgres — the connection string lands here from Aspire (audit DB)
// or compose (ConnectionStrings__audit env var).
builder.Services.AddDbContext<AuditDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("audit")));

builder.Services.AddAuditApplication();

// MassTransit + RabbitMQ. L1.B fills in AuditMassTransit.RegisterConsumers
// (an empty static stub from L0) — this gives L1.B a write seam without
// requiring any change to Program.cs at L1.B time.
builder.Services.AddMassTransit(cfg =>
{
    AuditMassTransit.RegisterConsumers(cfg);

    cfg.UsingRabbitMq((ctx, rabbit) =>
    {
        var amqp = builder.Configuration.GetConnectionString("rabbitmq")
                   ?? "amqp://guest:guest@localhost:5672/";
        rabbit.Host(amqp);
        rabbit.ConfigureEndpoints(ctx);
    });
});

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
    var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
    // L0 ships no migrations; the call is a no-op until L1.B adds the
    // partitioned-table migration. Wired now so the L1.B PR doesn't have
    // to touch Program.cs.
    await db.Database.MigrateAsync();
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
app.MapControllers();

app.Run();

public partial class Program { }
