using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.BuildingBlocks.Testing;

public static class OutboxAssert
{
    /// <summary>
    /// Verifies that an event has been stored in the Outbox table.
    /// This ensures transactional integrity as mandated by GEMINI.md.
    /// </summary>
    public static async Task AssertStoredInOutboxAsync<TDbContext>(
        IServiceProvider serviceProvider, 
        string schema = "public",
        int expectedCount = 1) where TDbContext : DbContext
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        
        // Use SqlQuery with FormattableString to satisfy EF1002.
        // We still need to be careful with the table name.
        // Since schema/tableName are usually internal test constants, it's relatively safe.
        var fullTableName = string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase)
            ? "\"OutboxMessage\""
            : $"\"{schema}\".\"OutboxMessage\"";

        // Query returns a list of results. Each result is an int.
        // Using SqlQueryRaw but with a constant string to avoid injection warnings where possible,
        // or just accept it as it's test infrastructure.
#pragma warning disable EF1002
        var counts = await db.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*)::int AS \"Value\" FROM {fullTableName}")
            .ToListAsync();
#pragma warning restore EF1002

        if (counts.Count == 0 || counts[0] < expectedCount)
        {
            throw new InvalidOperationException($"Expected at least {expectedCount} messages in outbox {fullTableName}, but found {(counts.Count > 0 ? counts[0].ToString() : "0")}.");
        }
    }
}
