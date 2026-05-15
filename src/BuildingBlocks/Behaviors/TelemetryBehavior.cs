using System.Diagnostics;
using System.Diagnostics.Metrics;
using MediatR;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace Haworks.BuildingBlocks.Behaviors;

/// <summary>
/// MediatR behavior for automatic OpenTelemetry instrumentation.
/// Starts an Activity for every request and records performance metrics.
/// </summary>
public class TelemetryBehavior<TRequest, TResponse>(
    ILogger<TelemetryBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly ActivitySource ActivitySource = new("Haworks.MediatR", "1.0.0");
    private static readonly Meter Meter = new("Haworks.MediatR", "1.0.0");
    
    private static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>(
        "mediatr.requests.total", 
        description: "Total number of MediatR requests");
        
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "mediatr.requests.duration", 
        unit: "ms", 
        description: "Duration of MediatR requests in milliseconds");

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        
        using var activity = ActivitySource.StartActivity($"MediatR: {requestName}");
        activity?.SetTag("mediatr.request.type", requestName);

        RequestCounter.Add(1, new KeyValuePair<string, object?>("request", requestName));
        
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await next();
            
            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            
            logger.LogError(ex, "Unhandled exception for request {RequestName}", requestName);
            throw;
        }
        finally
        {
            sw.Stop();
            RequestDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("request", requestName));
        }
    }
}
