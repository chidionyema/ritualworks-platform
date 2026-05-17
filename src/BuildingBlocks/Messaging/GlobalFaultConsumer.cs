using System.Diagnostics.Metrics;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Messaging;

/// <summary>
/// Catches ALL faulted messages across the bus, emits structured error logs,
/// and increments fault metrics with exception classification for alerting.
/// </summary>
public sealed class GlobalFaultConsumer(ILogger<GlobalFaultConsumer> logger) : IConsumer<Fault>
{
    private static readonly Meter Meter = new("Haworks.MassTransit", "1.0.0");
    private static readonly Counter<long> FaultCounter = Meter.CreateCounter<long>(
        "masstransit.faults.total",
        description: "Total faulted messages by consumer and exception type");

    private static readonly HashSet<string> TransientExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.TimeoutException",
        "System.Net.Http.HttpRequestException",
        "Npgsql.NpgsqlException",
        "RabbitMQ.Client.Exceptions.AlreadyClosedException",
        "System.IO.IOException",
        "System.OperationCanceledException",
    };

    public Task Consume(ConsumeContext<Fault> context)
    {
        var fault = context.Message;
        var exceptions = fault.Exceptions ?? [];
        var consumerType = context.SourceAddress?.Segments.LastOrDefault() ?? "Unknown";

        foreach (var ex in exceptions)
        {
            var isTransient = TransientExceptions.Contains(ex.ExceptionType);

            FaultCounter.Add(1,
                new KeyValuePair<string, object?>("consumer", consumerType),
                new KeyValuePair<string, object?>("exception_type", ex.ExceptionType),
                new KeyValuePair<string, object?>("is_transient", isTransient));

            logger.LogError(
                "DLQ: Faulted message (FaultId={FaultId}, MessageId={MessageId}, Consumer={Consumer}, " +
                "Transient={IsTransient}). Exception: {ExceptionType}: {ExceptionMessage}",
                fault.FaultId,
                context.MessageId,
                consumerType,
                isTransient,
                ex.ExceptionType,
                ex.Message);
        }

        if (exceptions.Length == 0)
        {
            FaultCounter.Add(1,
                new KeyValuePair<string, object?>("consumer", consumerType),
                new KeyValuePair<string, object?>("exception_type", "None"),
                new KeyValuePair<string, object?>("is_transient", false));

            logger.LogError(
                "DLQ: Faulted message (FaultId={FaultId}, MessageId={MessageId}, Consumer={Consumer}) — no exception details",
                fault.FaultId,
                context.MessageId,
                consumerType);
        }

        return Task.CompletedTask;
    }
}
