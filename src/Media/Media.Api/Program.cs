using Amazon.S3;
using Haworks.BuildingBlocks.Behaviors;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Startup;
using Haworks.Media.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Platform defaults (OTel, service discovery, health, correlation IDs) ──
builder.AddServiceDefaults();

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
    .AddDbHealthCheck<MediaDbContext>();

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
        // Return a no-op client; S3Service.GeneratePreSignedUrl guards the Enabled flag
        // so GetPreSignedURL is never actually called when disabled.
        return new AmazonS3Client(
            "disabled", "disabled",
            new AmazonS3Config { ServiceURL = "http://localhost:9999", ForcePathStyle = true });
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
