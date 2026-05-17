using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly;

namespace Haworks.BuildingBlocks.Pipelines;

public abstract class ThreePhaseHandlerBase<TRequest, TResponse, TDbContext>(
    TDbContext context,
    IAsyncPolicy resiliencePolicy,
    ILogger logger) : IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TDbContext : DbContext
{
    public async Task<TResponse> Handle(TRequest request, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        object? pendingState = null;

        // PHASE 1: LOCAL LOCK & STATE PREPARATION
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            pendingState = await PrepareAndLockAsync(request, ct);
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        if (pendingState == null)
            return await HandleIdempotentShortCircuitAsync(request, ct);

        // PHASE 2: RESILIENT EXTERNAL I/O (outside DB locks)
        GatewayResult gatewayResult;
        try
        {
            gatewayResult = await resiliencePolicy.ExecuteAsync(
                () => ExecuteExternalCallAsync(request, pendingState, ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Phase 2 gateway exhausted retries");
            gatewayResult = GatewayResult.Fail(ex.Message);
        }

        // PHASE 3: SETTLEMENT & OUTBOX RECONCILIATION
        TResponse result = default!;
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            result = await SettleAsync(request, pendingState, gatewayResult, ct);
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        return result;
    }

    protected abstract Task<object?> PrepareAndLockAsync(TRequest request, CancellationToken ct);
    protected abstract Task<TResponse> HandleIdempotentShortCircuitAsync(TRequest request, CancellationToken ct);
    protected abstract Task<GatewayResult> ExecuteExternalCallAsync(TRequest request, object pendingState, CancellationToken ct);
    protected abstract Task<TResponse> SettleAsync(TRequest request, object pendingState, GatewayResult gatewayResult, CancellationToken ct);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:DoNotDeclareStaticMembersOnGenericTypes", Justification = "Factory methods on nested result record are idiomatic")]
    public sealed record GatewayResult(bool IsSuccess, string? ExternalId, string? ErrorMessage)
    {
        public static GatewayResult Success(string externalId) => new(true, externalId, null);
        public static GatewayResult Fail(string error) => new(false, null, error);
    }
}
