using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Behaviors;
using Haworks.Analytics.Api.Infrastructure.Buffer;
using Haworks.Analytics.Api.Infrastructure.BackgroundServices;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .Enrich.FromLogContext();
});

// Auth
builder.Services.AddPlatformAuthentication(builder.Configuration);

// MediatR & Validation
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
});
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

// Kafka producer (connection name matches ConnectionStrings:kafka in config)
if (builder.Configuration.GetConnectionString("kafka") is not null)
{
    builder.AddKafkaProducer<string, string>("kafka");
}

// Analytics Infrastructure
builder.Services.AddSingleton<IEventBuffer, ChannelEventBuffer>();
builder.Services.AddHostedService<KafkaFlushingService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }
