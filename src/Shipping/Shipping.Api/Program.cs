using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Shipping.Api.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

builder.Services.AddPlatformAuthentication(builder.Configuration);

// ── Database ──
var connectionString = builder.Configuration.GetConnectionString("shipping")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:shipping is missing.");

builder.Services.AddDbContext<ShippingDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbHealthCheck<ShippingDbContext>();

// ── EasyPost ──
builder.Services.AddOptions<EasyPostOptions>()
    .Bind(builder.Configuration.GetSection(EasyPostOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IShippingProvider, EasyPostShippingProvider>();

// ── MassTransit ──
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddMassTransit(mt =>
    {
        mt.SetKebabCaseEndpointNameFormatter();
        mt.AddConsumer<GlobalFaultConsumer>();

        mt.AddEntityFrameworkOutbox<ShippingDbContext>(o =>
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

var app = builder.Build();

if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await db.Database.MigrateWithRetryAsync(logger);
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }
