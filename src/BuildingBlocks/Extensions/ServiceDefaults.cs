using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Haworks.BuildingBlocks.Extensions;

/// <summary>
/// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
/// This project should be referenced by each service project in the solution.
/// </summary>
public static class ServiceDefaults
{
    /// <summary>
    /// Adds service defaults to the host application builder.
    /// </summary>
    /// <remarks>
    /// This method adds the following services:
    /// <list type="bullet">
    ///   <item>Default health checks</item>
    ///   <item>OpenTelemetry metrics and tracing</item>
    ///   <item>Service discovery</item>
    ///   <item>Resilient HTTP client defaults</item>
    /// </list>
    /// </remarks>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry for the application.
    /// </summary>
    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    // Uncomment the following line to enable gRPC instrumentation
                    // (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    // .AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource("MassTransit");
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        // if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        // {
        //     builder.Services.AddOpenTelemetry()
        //        .UseAzureMonitor();
        // }

        return builder;
    }

    /// <summary>
    /// Adds default health checks to the application.
    /// </summary>
    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps default health check endpoints.
    /// </summary>
    /// <remarks>
    /// Maps the following endpoints:
    /// <list type="bullet">
    ///   <item>/health - All health checks</item>
    ///   <item>/health/ready - Readiness checks (databases must be connected)</item>
    ///   <item>/health/live - Liveness checks only (app is responsive)</item>
    /// </list>
    /// </remarks>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // All health checks must pass for detailed health info
        app.MapHealthChecks("/health");

        // Readiness check - all database contexts must be connected
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready")
        });

        // Liveness check - only checks if app is responsive
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        // Legacy endpoint for backwards compatibility
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        return app;
    }
}
