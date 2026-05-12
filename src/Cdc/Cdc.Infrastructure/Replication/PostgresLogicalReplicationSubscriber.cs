using Haworks.Cdc.Application.Interfaces;
using Haworks.Contracts.Cdc;
using MassTransit;
using Microsoft.Extensions.Logging;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;

namespace Haworks.Cdc.Infrastructure.Replication;

public sealed class PostgresLogicalReplicationSubscriber : ICdcRelay
{
    private readonly ReplicationOptions _options;
    private readonly IBus _bus;
    private readonly ILogger<PostgresLogicalReplicationSubscriber> _logger;
    private readonly PgOutputDecoder _decoder;

    public PostgresLogicalReplicationSubscriber(
        ReplicationOptions options,
        IBus bus,
        ILogger<PostgresLogicalReplicationSubscriber> logger)
    {
        _options = options;
        _bus = bus;
        _logger = logger;
        _decoder = new PgOutputDecoder(options.SourceService);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunReplicationLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CDC replication loop failed for {SourceService}. Retrying in 5s...", _options.SourceService);
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task RunReplicationLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting logical replication for {SourceService} using slot {SlotName}", 
            _options.SourceService, _options.SlotName);

        await using var conn = new LogicalReplicationConnection(_options.ConnectionString);
        await conn.Open(ct);

        var slot = new PgOutputReplicationSlot(_options.SlotName);

        // Protocol version 4 adds support for parallel streaming in Postgres 16+
        var options = new PgOutputReplicationOptions(_options.PublicationName, PgOutputProtocolVersion.V1);

        await foreach (var message in conn.StartReplication(slot, options, ct))
        {
            var cdcEvent = await _decoder.DecodeAsync(message, ct);

            if (cdcEvent != null)
            {
                await _bus.Publish(cdcEvent, ct);

                _logger.LogDebug("Published CDC event {EventId} for {EntityType} {EntityId}", 
                    cdcEvent.EventId, cdcEvent.EntityType, cdcEvent.EntityId);
            }

            // Always acknowledge to allow Postgres to reclaim WAL.
            // At-least-once: we ack AFTER publish.
            conn.SetReplicationStatus(message.WalEnd);
            
            if (message is Npgsql.Replication.PgOutput.Messages.CommitMessage)
            {
                // Optimization: only send status update to server on commit
                await conn.SendStatusUpdate(ct);
            }
        }
    }
}
