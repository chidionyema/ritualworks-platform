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
    private readonly Channel<AuditRow> _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AuditWriter> _logger;
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _cts = new();

    public AuditWriter(IServiceProvider serviceProvider, ILogger<AuditWriter> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _channel = Channel.CreateUnbounded<AuditRow>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
        _workerTask = Task.Run(ProcessBatchAsync);
    }

    public async ValueTask WriteAsync(AuditRow row, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync(row, ct);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        _channel.Writer.TryComplete();
        await _workerTask;
    }

    private async Task ProcessBatchAsync()
    {
        var batch = new List<AuditRow>(50);
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_cts.Token))
            {
                while (batch.Count < 50 && _channel.Reader.TryRead(out var row))
                {
                    batch.Add(row);
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
            while (_channel.Reader.TryRead(out var row))
            {
                batch.Add(row);
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

    private async Task WriteBatchAsync(List<AuditRow> batch)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY audit_events (id, occurred_at, received_at, event_type, entity_type, entity_id, actor_id, actor_type, correlation_id, payload, metadata) FROM STDIN (FORMAT BINARY)");

        try
        {
            foreach (var row in batch)
            {
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
