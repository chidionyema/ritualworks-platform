namespace Haworks.BuildingBlocks.Idempotency;

/// <summary>
/// Represents a processed command in the idempotency journal.
/// Stored in a shared table with a unique index on <see cref="IdempotencyKey"/>.
/// TTL: entries older than <see cref="ExpiresAt"/> are eligible for cleanup.
/// </summary>
public sealed class IdempotencyJournalEntry
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public required string IdempotencyKey { get; init; }
    public required string CommandType { get; init; }
    public string? ResponseJson { get; set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; private set; }

    public static IdempotencyJournalEntry Create(string key, string commandType, TimeSpan ttl)
    {
        return new IdempotencyJournalEntry
        {
            IdempotencyKey = key,
            CommandType = commandType,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl),
        };
    }
}
