using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Messaging;

/// <summary>
/// Catches ALL faulted messages across the bus and emits structured error logs.
/// Without this, messages landing in *_error queues are invisible unless
/// someone monitors the RabbitMQ Management UI.
/// </summary>
public sealed class GlobalFaultConsumer(ILogger<GlobalFaultConsumer> logger) : IConsumer<Fault>
{
    public Task Consume(ConsumeContext<Fault> context)
    {
        var fault = context.Message;
        var exceptions = fault.Exceptions ?? [];

        foreach (var ex in exceptions)
        {
            logger.LogError(
                "DLQ: Faulted message (FaultId={FaultId}, MessageId={MessageId}). " +
                "Exception: {ExceptionType}: {ExceptionMessage}",
                fault.FaultId,
                context.MessageId,
                ex.ExceptionType,
                ex.Message);
        }

        if (exceptions.Length == 0)
        {
            logger.LogError(
                "DLQ: Faulted message (FaultId={FaultId}, MessageId={MessageId}) — no exception details",
                fault.FaultId,
                context.MessageId);
        }

        return Task.CompletedTask;
    }
}
