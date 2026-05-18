using Haworks.Audit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Audit.Infrastructure.Partitions;

public class PartitionRolloverService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PartitionRolloverService> _logger;

    public PartitionRolloverService(
        IServiceProvider serviceProvider,
        TimeProvider timeProvider,
        ILogger<PartitionRolloverService> logger)
    {
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EnsurePartitionsExistAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to ensure audit partitions exist");
                }

                await Task.Delay(TimeSpan.FromHours(24), _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private async Task EnsurePartitionsExistAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var now = _timeProvider.GetUtcNow();
        // Ensure partitions for current month and next month
        await CreatePartitionForMonthAsync(db, now.Year, now.Month, ct);
        
        var nextMonth = now.AddMonths(1);
        await CreatePartitionForMonthAsync(db, nextMonth.Year, nextMonth.Month, ct);
    }

    private async Task CreatePartitionForMonthAsync(AuditDbContext db, int year, int month, CancellationToken ct)
    {
        var partitionName = $"audit_events_{year}_{month:D2}";
        var fromDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-dd");
        var toDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).ToString("yyyy-MM-dd");

        var sql = $@"
            CREATE TABLE IF NOT EXISTS audit.{partitionName} PARTITION OF audit.audit_events
            FOR VALUES FROM ('{fromDate}') TO ('{toDate}');
            
            CREATE INDEX IF NOT EXISTS {partitionName}_entity_idx 
            ON audit.{partitionName} (entity_type, entity_id, occurred_at DESC);
            
            CREATE INDEX IF NOT EXISTS {partitionName}_event_type_idx 
            ON audit.{partitionName} (event_type, occurred_at DESC);

            CREATE UNIQUE INDEX IF NOT EXISTS audit_events_msg_id_uniq_{year}_{month:D2}
            ON audit.{partitionName} ((metadata->>'message_id'))
            WHERE metadata->>'message_id' IS NOT NULL;
        ";

        try
        {
            #pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(sql, Array.Empty<object>());
            #pragma warning restore EF1002
            _logger.LogInformation("Ensured partition {PartitionName} exists", partitionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Partition {PartitionName} creation failed (may already exist)", partitionName);
        }
    }
}
