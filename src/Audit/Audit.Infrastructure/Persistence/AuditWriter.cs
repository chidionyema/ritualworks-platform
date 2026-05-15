using System.Text.Json;
using System.Threading.Channels;
using Haworks.Audit.Application.Capture;
using Haworks.Audit.Application.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Haworks.Audit.Infrastructure.Persistence;

public sealed class AuditWriter : IAuditWriter, IAsyncDisposable
{
    private const int MaxRetries = 3;

    private readonly record struct PendingRow(AuditRow Row, int RetryCount);

    private readonly Channel<PendingRow> _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AuditWriter> _logger;
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _cts = new();

    public AuditWriter(IServiceProvider serviceProvider, ILogger<AuditWriter> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _channel = Channel.CreateUnbounded<PendingRow>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
        _workerTask = Task.Run(ProcessBatchAsync);
    }

    public async ValueTask WriteAsync(AuditRow row, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync(new PendingRow(row, 0), ct);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        _channel.Writer.TryComplete();
        await _workerTask;
    }

    private async Task ProcessBatchAsync()
    {
        var batch = new List<PendingRow>(50);
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_cts.Token))
            {
                while (batch.Count < 50 && _channel.Reader.TryRead(out var pending))
                {
                    batch.Add(pending);
                }

                if (batch.Count > 0)
                {
                    await WriteBatchAsync(batch);
                    batch.Clear();
                }

                await Task.Delay(200, _cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AuditWriter worker failed");
        }
        finally
        {
            while (_channel.Reader.TryRead(out var pending))
            {
                batch.Add(pending);
                if (batch.Count >= 50)
                {
                    await WriteBatchAsync(batch);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                await WriteBatchAsync(batch);
            }
        }
    }

    private async Task WriteBatchAsync(List<PendingRow> batch)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();

            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            // Deduplicate within the batch by message_id before COPY.
            // COPY is all-or-nothing — a duplicate message_id in the same batch
            // violates the unique index and rejects ALL rows.
            var seen = new HashSet<string>();
            var deduped = new List<PendingRow>();
            foreach (var p in batch)
            {
                var meta = p.Row.Metadata;
                var msgId = meta.TryGetProperty("message_id", out var mid) ? mid.GetString() : null;
                if (msgId == null || seen.Add(msgId))
                    deduped.Add(p);
                else
                    _logger.LogDebug("AuditWriter: skipping duplicate message_id {MessageId} within batch", msgId);
            }

            await using var writer = await connection.BeginBinaryImportAsync(
                "COPY audit.audit_events (id, occurred_at, received_at, event_type, entity_type, entity_id, actor_id, actor_type, correlation_id, payload, metadata) FROM STDIN (FORMAT BINARY)");

            try
            {
                foreach (var pending in deduped)
                {
                    var row = pending.Row;
                    await writer.StartRowAsync();
                    await writer.WriteAsync(Guid.NewGuid(), NpgsqlTypes.NpgsqlDbType.Uuid);
                    await writer.WriteAsync(row.OccurredAt, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                    await writer.WriteAsync(DateTimeOffset.UtcNow, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                    await writer.WriteAsync(row.EventType, NpgsqlTypes.NpgsqlDbType.Text);
                    await writer.WriteAsync(row.EntityType, NpgsqlTypes.NpgsqlDbType.Text);
                    await writer.WriteAsync(row.EntityId, NpgsqlTypes.NpgsqlDbType.Text);
                    await writer.WriteAsync(row.ActorId, NpgsqlTypes.NpgsqlDbType.Text);
                    await writer.WriteAsync(row.ActorType, NpgsqlTypes.NpgsqlDbType.Text);
                    await writer.WriteAsync(row.CorrelationId, NpgsqlTypes.NpgsqlDbType.Text);
                    await writer.WriteAsync(JsonSerializer.Serialize(row.Payload), NpgsqlTypes.NpgsqlDbType.Jsonb);
                    await writer.WriteAsync(JsonSerializer.Serialize(row.Metadata), NpgsqlTypes.NpgsqlDbType.Jsonb);
                }

                await writer.CompleteAsync();
            }
            catch
            {
                try { await writer.CloseAsync(); } catch { }
                throw;
            }
        }
        catch (PostgresException ex)
        {
            // Re-queue items that have not yet exceeded the retry limit; dead-letter the rest.
            var requeued = 0;
            var dropped = 0;
            foreach (var pending in batch)
            {
                var nextRetry = pending.RetryCount + 1;
                if (nextRetry >= MaxRetries)
                {
                    dropped++;
                    _logger.LogError(ex,
                        "AuditWriter: dropping audit row after {MaxRetries} failed attempts. EventType={EventType} EntityId={EntityId}",
                        MaxRetries, pending.Row.EventType, pending.Row.EntityId);
                }
                else
                {
                    requeued++;
                    _channel.Writer.TryWrite(new PendingRow(pending.Row, nextRetry));
                }
            }

            if (requeued > 0)
            {
                _logger.LogWarning(ex,
                    "AuditWriter: COPY batch failed, re-queued {Requeued} rows, dropped {Dropped} rows (max retries exhausted)",
                    requeued, dropped);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _workerTask;
        }
        catch { }
        _cts.Dispose();
    }
}
