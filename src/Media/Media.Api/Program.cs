using Amazon.S3;
using Haworks.BuildingBlocks.Behaviors;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Messaging;
using MassTransit;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Startup;
using Haworks.Media.Api.Infrastructure;
using Haworks.Media.Api.Infrastructure.Processing;
using Haworks.Media.Api.Infrastructure.Workers;
using Haworks.Media.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Platform defaults (OTel, service discovery, health, correlation IDs) ──
builder.AddServiceDefaults();

// ── TimeProvider (soft-delete stamping, testability) ──
builder.Services.TryAddSingleton(TimeProvider.System);

// ── Serilog ──
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

// ── Authentication / authorisation ──
builder.Services.AddPlatformAuthentication(builder.Configuration);

// ── Database ──
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "No database connection string. Expected 'ConnectionStrings:DefaultConnection'.");

builder.Services.AddDbContext<MediaDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── Health checks ──
builder.Services.AddHealthChecks()
    .AddDbHealthCheck<MediaDbContext>()
    .AddCheck<Haworks.Media.Api.Infrastructure.Health.ClamAvHealthCheck>("clamav")
    .AddCheck<Haworks.Media.Api.Infrastructure.Health.S3HealthCheck>("s3");

// ── Upload options ──
builder.Services.AddOptions<UploadOptions>()
    .Bind(builder.Configuration.GetSection(UploadOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ── S3 storage ──
builder.Services.AddOptions<MediaStorageOptions>()
    .Bind(builder.Configuration.GetSection(MediaStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<MediaStorageOptions>>().Value;
    if (!opts.Enabled)
    {
        return new AmazonS3Client(
            "disabled", "disabled",
            new AmazonS3Config { ServiceURL = "http://disabled.invalid:9999", ForcePathStyle = true });
    }

    var cfg = new AmazonS3Config
    {
        AuthenticationRegion = opts.Region,
        ForcePathStyle = true,
        UseHttp = !string.IsNullOrEmpty(opts.ServiceUrl)
                  && opts.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
    };
    if (!string.IsNullOrEmpty(opts.ServiceUrl))
        cfg.ServiceURL = opts.ServiceUrl;

    // If AccessKey is empty, use IAM role credentials (ECS task role, EC2 instance profile, IRSA).
    // This is the preferred path in production — no static credentials stored in config.
    if (string.IsNullOrEmpty(opts.AccessKey))
        return new AmazonS3Client(cfg);

    return new AmazonS3Client(opts.AccessKey, opts.SecretKey, cfg);
});

builder.Services.AddScoped<IS3Service, S3Service>();

// ── ClamAV virus scanner ──
builder.Services.AddOptions<ClamAvOptions>()
    .Bind(builder.Configuration.GetSection(ClamAvOptions.SectionName));

builder.Services.AddScoped<IVirusScanner, ClamAvScanner>();

// ── MediatR + pipeline behaviours ──
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});

builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

// ── Media processing pipeline ──
builder.Services.AddOptions<TranscodeOptions>()
    .Bind(builder.Configuration.GetSection(TranscodeOptions.SectionName));
builder.Services.AddOptions<ImageOptions>()
    .Bind(builder.Configuration.GetSection(ImageOptions.SectionName));

builder.Services.AddSingleton<FfmpegService>();
builder.Services.AddScoped<IMediaProcessor, ImageProcessor>();
builder.Services.AddScoped<IMediaProcessor, VideoProcessor>();
builder.Services.AddScoped<IMediaProcessor, AudioProcessor>();
builder.Services.AddScoped<MediaProcessingOrchestrator>();

// ── S3 event notifications (SQS consumer) ──
builder.Services.AddOptions<S3NotificationOptions>()
    .Bind(builder.Configuration.GetSection(S3NotificationOptions.SectionName));

// ── Background workers ──
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddHostedService<UploadSweeperWorker>();

    var s3NotifyOpts = builder.Configuration
        .GetSection(S3NotificationOptions.SectionName)
        .Get<S3NotificationOptions>();

    if (s3NotifyOpts?.Enabled == true)
    {
        builder.Services.AddSingleton<Amazon.SQS.IAmazonSQS>(sp =>
        {
            var cfg = new Amazon.SQS.AmazonSQSConfig { RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(s3NotifyOpts.Region) };
            var storageOpts = sp.GetRequiredService<IOptions<MediaStorageOptions>>().Value;
            if (!string.IsNullOrEmpty(storageOpts.ServiceUrl) && storageOpts.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                cfg.ServiceURL = storageOpts.ServiceUrl; // LocalStack
            return new Amazon.SQS.AmazonSQSClient(storageOpts.AccessKey, storageOpts.SecretKey, cfg);
        });
        builder.Services.AddHostedService<S3EventConsumer>();
    }
}

// ── MassTransit + outbox (event publishing) ──
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddMassTransit(mt =>
    {
        mt.SetKebabCaseEndpointNameFormatter();
        mt.AddDelayedMessageScheduler();
        mt.AddConsumer<Haworks.BuildingBlocks.Messaging.GlobalFaultConsumer>();
        mt.AddConsumer<Haworks.Media.Api.Infrastructure.Workers.ProcessMediaConsumer>();
        mt.AddConsumer<Haworks.Media.Api.Infrastructure.Workers.ProcessMediaFaultConsumer>();
        mt.AddConsumer<Haworks.Media.Api.Infrastructure.Workers.MediaUploadCompletedConsumer>();

        mt.AddEntityFrameworkOutbox<MediaDbContext>(o =>
        {
            o.UsePostgres();
            o.UseBusOutbox();
            o.QueryDelay = TimeSpan.FromMilliseconds(100);
            o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
        });

        mt.UsingRabbitMq((context, cfg) =>
        {
            var rabbitConn = builder.Configuration.GetConnectionString("rabbitmq")
                ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is missing.");
            cfg.Host(new Uri(rabbitConn));
            cfg.UseDelayedMessageScheduler();
            cfg.ConfigureStandardRabbitMq(context);
        });
    });
}

builder.Services.AddDomainEventPublisher();

// ── Startup task runner (EF migrations with retry) ──
builder.Services.AddStartupTaskRunner();

// ── API ──
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── EF migration on startup (skipped in Test environment) ──
if (!app.Environment.IsEnvironment("Test"))
{
    var startupRunner = app.Services.GetRequiredService<StartupTaskRunner>();
    startupRunner.AddTask(async (sp, ct) =>
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await db.Database.MigrateWithRetryAsync(logger, ct);
    });
}

// ── Middleware pipeline ──
app.MapDefaultEndpoints();   // /health, /health/ready, /health/live, correlation ID

app.UseHttpsRedirection();

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
