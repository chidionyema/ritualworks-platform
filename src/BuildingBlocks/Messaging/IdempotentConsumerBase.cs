using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Messaging;

public abstract class IdempotentConsumerBase<TEvent, TDbContext>(
    TDbContext context,
    ILogger logger) : IConsumer<TEvent>
    where TEvent : class
    where TDbContext : DbContext
{
    protected TDbContext DbContext { get; } = context;

    public async Task Consume(ConsumeContext<TEvent> consumeContext)
    {
        var message = consumeContext.Message;
        var idempotencyKey = ResolveIdempotencyKey(message);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            logger.LogCritical("Consumer dropped message: Idempotency key resolved to null for event {EventName}", typeof(TEvent).Name);
            return;
        }

        // Do NOT call BeginTransactionAsync — MassTransit EF Outbox manages the
        // ambient transaction. Manual transactions conflict with the outbox and
        // can cause nested transactions or outbox messages to commit separately.
        try
        {
            if (await IsAlreadyProcessedAsync(idempotencyKey, consumeContext.CancellationToken))
            {
                logger.LogInformation("Idempotent skip for key {Key}", idempotencyKey);
                return;
            }

            await ExecuteBusinessLogicAsync(consumeContext, consumeContext.CancellationToken);
            await RecordProcessedAsync(idempotencyKey, consumeContext.CancellationToken);

            // MassTransit EF Outbox calls SaveChangesAsync and commits the
            // ambient transaction when Consume returns successfully.
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            logger.LogWarning(ex, "Concurrency guard: duplicate {Key} caught at DB index layer", idempotencyKey);
        }
    }

    protected abstract string ResolveIdempotencyKey(TEvent message);
    protected abstract Task<bool> IsAlreadyProcessedAsync(string key, CancellationToken ct);
    protected abstract Task ExecuteBusinessLogicAsync(ConsumeContext<TEvent> context, CancellationToken ct);
    protected abstract Task RecordProcessedAsync(string key, CancellationToken ct);

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
