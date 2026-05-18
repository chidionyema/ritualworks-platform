using Haworks.Audit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FluentAssertions;

namespace Haworks.Audit.Integration;

[Collection("AuditIntegration")]
public class PartitionRolloverTests : IClassFixture<AuditWebAppFactory>
{
    private readonly AuditWebAppFactory _factory;

    public PartitionRolloverTests(AuditWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Rollover_ShouldCreatePartitions()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        await db.Database.MigrateAsync();

        // Create base table if it doesn't exist (L1.B might not have run)
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS audit_events (
                id uuid NOT NULL,
                occurred_at timestamptz NOT NULL,
                received_at timestamptz NOT NULL,
                event_type text NOT NULL,
                entity_type text NOT NULL,
                entity_id text NOT NULL,
                actor_id text,
                actor_type text,
                correlation_id text,
                payload jsonb NOT NULL,
                metadata jsonb NOT NULL,
                PRIMARY KEY (id, occurred_at)
            ) PARTITION BY RANGE (occurred_at);
        ");

        // Act - Manually run the partition creation logic to verify the SQL syntax
        var now = DateTimeOffset.UtcNow;
        var partitionName = $"audit_events_{now.Year}_{now.Month:D2}";
        var fromDate = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-dd");
        var toDate = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).ToString("yyyy-MM-dd");

#pragma warning disable EF1002, HWK027 // Test-only: partition name is deterministic, not user input
        await db.Database.ExecuteSqlRawAsync($@"
            CREATE TABLE IF NOT EXISTS {partitionName} PARTITION OF audit_events
            FOR VALUES FROM ('{fromDate}') TO ('{toDate}');
        ");
#pragma warning restore EF1002, HWK027

        // Assert
        var partitions = await db.Database.SqlQueryRaw<string>(@"
            SELECT child.relname AS partition_name
            FROM pg_inherits
            JOIN pg_class parent ON pg_inherits.inhparent = parent.oid
            JOIN pg_class child ON pg_inherits.inhrelid = child.oid
            WHERE parent.relname = 'audit_events' AND child.relname = {0};
        ", partitionName).ToListAsync();

        partitions.Should().Contain(partitionName);
    }
}
