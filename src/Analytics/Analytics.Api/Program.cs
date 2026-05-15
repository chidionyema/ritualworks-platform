using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Behaviors;
using Haworks.Analytics.Api.Infrastructure.Buffer;
using Haworks.Analytics.Api.Infrastructure.BackgroundServices;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// MediatR & Validation
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
});
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

// Analytics Infrastructure
builder.Services.AddSingleton<IEventBuffer, ChannelEventBuffer>();
builder.Services.AddHostedService<KafkaFlushingService>();

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

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
// removed UseSwagger
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();

public partial class Program { }
