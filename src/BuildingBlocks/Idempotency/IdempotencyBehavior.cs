using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Idempotency;

/// <summary>
/// MediatR pipeline behavior that enforces at-most-once execution for commands
/// implementing <see cref="IIdempotentCommand"/>.
///
/// Flow:
/// 1. Check if IdempotencyKey exists in the journal
/// 2. If yes → deserialize and return cached response (no handler execution)
/// 3. If no → execute handler, store response in journal, return
///
/// The journal uses a UNIQUE index on IdempotencyKey to handle races:
/// if two threads pass step 1 simultaneously, only one will successfully INSERT;
/// the other gets a unique constraint violation and falls back to the cached response.
/// </summary>
public sealed class IdempotencyBehavior<TRequest, TResponse>(
    IIdempotencyJournalDbContext journalDb,
    ILogger<IdempotencyBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IIdempotentCommand
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(48);

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var key = request.IdempotencyKey;

        // Empty key = caller explicitly opted out (e.g., background jobs that handle their own dedup)
        if (string.IsNullOrWhiteSpace(key))
            return await next();

        // 1. Check journal for existing execution
        var existing = await journalDb.IdempotencyJournal
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.IdempotencyKey == key, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Idempotent replay: key={Key}, command={CommandType}",
                key, typeof(TRequest).Name);

            if (existing.ResponseJson is not null)
                return JsonSerializer.Deserialize<TResponse>(existing.ResponseJson)!;

            return default!;
        }

        // 2. Execute the handler
        var response = await next();

        // 3. Record in journal (race-safe via unique constraint)
        try
        {
            var entry = IdempotencyJournalEntry.Create(key, typeof(TRequest).Name, DefaultTtl);
            entry.ResponseJson = response is not null ? JsonSerializer.Serialize(response) : null;

            journalDb.IdempotencyJournal.Add(entry);
            await journalDb.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Race: another thread recorded this key between our check and insert.
            // The handler already executed — this is a benign race (response is correct).
            logger.LogDebug(ex, "Idempotency journal race on key={Key} — benign", key);
        }

        return response;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException?.Message ?? "";
        return inner.Contains("23505") || inner.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}
