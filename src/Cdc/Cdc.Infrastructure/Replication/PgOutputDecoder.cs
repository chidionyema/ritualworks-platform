using System.Text.Json;
using System.Text.Json.Nodes;
using Haworks.Contracts.Cdc;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;

namespace Haworks.Cdc.Infrastructure.Replication;

public sealed class PgOutputDecoder
{
    private string _currentTransactionId = "unknown";
    private readonly string _sourceService;

    public PgOutputDecoder(string sourceService)
    {
        _sourceService = sourceService;
    }

    public async Task<EntityChangedEvent?> DecodeAsync(ReplicationMessage message, CancellationToken ct = default)
    {
        if (message is BeginMessage begin)
        {
            _currentTransactionId = begin.TransactionXid.ToString();
            return null;
        }

        if (message is RelationMessage)
        {
            // Npgsql 9.0 handles relation metadata automatically via ReplicationValue.GetFieldName()
            return null;
        }

        if (message is InsertMessage insert)
            return await DecodeInsertAsync(insert, ct);
        if (message is UpdateMessage update)
            return await DecodeUpdateAsync(update, ct);
        if (message is DeleteMessage delete)
            return await DecodeDeleteAsync(delete, ct);

        return null;
    }

    private async Task<EntityChangedEvent> DecodeInsertAsync(InsertMessage msg, CancellationToken ct)
    {
        var payload = await ReadRowAsync(msg.NewRow, ct);

        return CreateEvent(
            entityType: msg.Relation.RelationName,
            entityId: ExtractId(payload),
            changeType: "created",
            payloadAfter: payload,
            transactionId: _currentTransactionId);
    }

    private async Task<EntityChangedEvent> DecodeUpdateAsync(UpdateMessage msg, CancellationToken ct)
    {
        var payloadAfter = await ReadRowAsync(msg.NewRow, ct);
        
        // In Npgsql 9.0, FullUpdateMessage contains OldRow
        JsonObject? payloadBefore = null;
        if (msg is FullUpdateMessage fullUpdate)
        {
            payloadBefore = await ReadRowAsync(fullUpdate.OldRow, ct);
        }

        return CreateEvent(
            entityType: msg.Relation.RelationName,
            entityId: ExtractId(payloadAfter),
            changeType: "updated",
            payloadBefore: payloadBefore,
            payloadAfter: payloadAfter,
            transactionId: _currentTransactionId);
    }

    private async Task<EntityChangedEvent> DecodeDeleteAsync(DeleteMessage msg, CancellationToken ct)
    {
        // In Npgsql 9.0, FullDeleteMessage contains OldRow
        JsonObject? payloadBefore = null;
        if (msg is FullDeleteMessage fullDelete)
        {
            payloadBefore = await ReadRowAsync(fullDelete.OldRow, ct);
        }

        return CreateEvent(
            entityType: msg.Relation.RelationName,
            entityId: ExtractId(payloadBefore),
            changeType: "deleted",
            payloadBefore: payloadBefore,
            payloadAfter: null,
            transactionId: _currentTransactionId);
    }

    private async Task<JsonObject?> ReadRowAsync(ReplicationTuple row, CancellationToken ct)
    {
        if (row == null) return null;

        var json = new JsonObject();
        await foreach (var value in row)
        {
            var fieldName = value.GetFieldName();
            var val = await value.Get<object>(ct);
            json[fieldName] = JsonValue.Create(val);
        }
        return json;
    }

    private EntityChangedEvent CreateEvent(
        string entityType,
        string entityId,
        string changeType,
        string transactionId,
        JsonNode? payloadBefore = null,
        JsonNode? payloadAfter = null)
    {
        return new EntityChangedEvent
        {
            EventId = Guid.NewGuid(),
            EntityType = entityType,
            EntityId = entityId,
            ChangeType = changeType,
            OccurredAt = DateTimeOffset.UtcNow,
            SourceService = _sourceService,
            SourceTransactionId = transactionId,
            PayloadBefore = payloadBefore,
            PayloadAfter = payloadAfter
        };
    }

    private string ExtractId(JsonObject? payload)
    {
        if (payload == null) return "unknown";
        
        if (payload.TryGetPropertyValue("id", out var idNode) && idNode != null)
            return idNode.ToString();
        if (payload.TryGetPropertyValue("Id", out var idNodeUpper) && idNodeUpper != null)
            return idNodeUpper.ToString();
            
        return "unknown";
    }
}
