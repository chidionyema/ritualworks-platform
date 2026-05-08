namespace Haworks.BuildingBlocks.Idempotency;

/// <summary>
/// Atomic idempotency-claim store. The contract is "first caller of a key
/// within the TTL window wins; everyone else sees a duplicate". The
/// implementation must use a real concurrency primitive (Postgres UNIQUE
/// + ON CONFLICT, Redis SETNX, etc.) -- not in-process state.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically attempt to claim <paramref name="key"/>. Returns
    /// <see cref="IdempotencyClaim.Winner"/> for the first caller and
    /// <see cref="IdempotencyClaim.Duplicate"/> for everyone after, all
    /// within the TTL window the winner provided.
    /// </summary>
    Task<IdempotencyClaim> TryClaimAsync(
        string key,
        TimeSpan ttl,
        CancellationToken ct = default);
}

public enum IdempotencyClaimStatus
{
    Winner = 0,
    Duplicate = 1,
}

public sealed record IdempotencyClaim(IdempotencyClaimStatus Status)
{
    public bool IsDuplicate => Status == IdempotencyClaimStatus.Duplicate;
    public static IdempotencyClaim Winner { get; } = new(IdempotencyClaimStatus.Winner);
    public static IdempotencyClaim Duplicate { get; } = new(IdempotencyClaimStatus.Duplicate);
}
