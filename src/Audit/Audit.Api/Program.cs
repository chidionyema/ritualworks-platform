using Haworks.Audit.Application;
using Haworks.Audit.Application.Capture;
using Haworks.Audit.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Startup;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Audit's Postgres — the connection string lands here from Aspire (audit DB)
// or compose (ConnectionStrings__audit env var).
builder.Services.AddDbContext<AuditDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("audit"));
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

builder.Services.AddAuditApplication();
builder.Services.AddStartupTaskRunner();

// MassTransit + RabbitMQ — gated behind Test env to allow test harness override
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddMassTransit(cfg =>
    {
        AuditMassTransit.RegisterConsumers(cfg);

        cfg.AddEntityFrameworkOutbox<Haworks.Audit.Infrastructure.Persistence.AuditDbContext>(o =>
        {
            o.UsePostgres().UseBusOutbox();
        });

        cfg.UsingRabbitMq((ctx, rabbit) =>
        {
            var amqp = builder.Configuration.GetConnectionString("rabbitmq")
                       ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is required");
            rabbit.Host(amqp);
            rabbit.ConfigureStandardRabbitMq(ctx);
        });
    });
}

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
    .AddDbHealthCheck<Haworks.Audit.Infrastructure.Persistence.AuditDbContext>();

var app = builder.Build();

if (!app.Environment.IsEnvironment("Test"))
{
    var startupRunner = app.Services.GetRequiredService<StartupTaskRunner>();
    startupRunner.AddTask(async (sp, ct) =>
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        // L0 ships no migrations; the call is a no-op until L1.B adds the
        // partitioned-table migration. Wired now so the L1.B PR doesn't have
        // to touch Program.cs.
        await db.Database.MigrateAsync(ct);
    });
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
