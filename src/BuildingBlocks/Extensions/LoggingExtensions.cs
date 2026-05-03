using Microsoft.AspNetCore.Builder;
using Serilog;
using Serilog.Events;

namespace Haworks.BuildingBlocks.Extensions;

public static class LoggingExtensions
{
    public static void ConfigureLogging(this WebApplicationBuilder builder)
    {
        // Log directory is configurable so the same code runs on a developer's
        // machine (where `/logs` isn't writable) and inside a container (where
        // it traditionally is). Resolution order: `Logging:Directory` config →
        // `LOG_DIR` env var → `./logs` relative to the working directory. The
        // hardcoded `/logs/...` here used to silently no-op or, depending on
        // the Serilog version, take down the host at startup.
        var logDirectory =
            builder.Configuration["Logging:Directory"]
            ?? Environment.GetEnvironmentVariable("LOG_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "logs");

        try
        {
            Directory.CreateDirectory(logDirectory);
        }
        catch
        {
            // If we can't create the configured directory (read-only mount,
            // permissions), fall back to the system temp dir so logging
            // never blocks startup.
            logDirectory = Path.Combine(Path.GetTempPath(), "haworks-logs");
            Directory.CreateDirectory(logDirectory);
        }

        var logFilePath = Path.Combine(logDirectory, "app-log-.txt");

        builder.Host.UseSerilog((context, config) =>
            config.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day));
    }
}