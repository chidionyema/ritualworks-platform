using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Haworks.Audit.Application.Extraction;
using Haworks.Audit.Application.Redaction;

using Haworks.Contracts;
using MassTransit;

namespace Haworks.Audit.Application.Capture;

/// <summary>
/// Generic consumer that captures any <see cref="IDomainEvent"/> into the
/// audit log. It orchestrates extraction, redaction, and batched writing.
///
/// Per spec § 5.3, it handles idempotency via <c>message_id</c> in metadata.
/// </summary>
public sealed class AuditConsumer<T> : IConsumer<T> where T : class, IDomainEvent
{
    private readonly IAuditExtractor<T> _extractor;
    private readonly ISecretRedactor _redactor;
    private readonly IAuditWriter _writer;

    public AuditConsumer(
        IAuditExtractor<T> extractor,
        ISecretRedactor redactor,
        IAuditWriter writer)
    {
        _extractor = extractor;
        _redactor = redactor;
        _writer = writer;
    }

    public async Task Consume(ConsumeContext<T> context)
    {
        // 1. Extract raw row from event
        var row = _extractor.Extract(context.Message, context);

        // 2. Redact secrets from payload
        var redactedPayload = _redactor.Redact(row.Payload);

        // 3. Prepare metadata (including message_id for idempotency)
        var metadataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(row.Metadata.GetRawText()) ?? new();
        
        if (context.MessageId.HasValue)
        {
            metadataDict["message_id"] = context.MessageId.Value.ToString();
        }
        else if (!metadataDict.ContainsKey("message_id"))
        {
            metadataDict["message_id"] = GenerateDeterministicHash(row.EventType, row.Payload, row.OccurredAt);
        }

        // Add transport metadata
        metadataDict["rabbitMqRoutingKey"] = context.RoutingKey() ?? string.Empty;
        metadataDict["publishedBy"] = context.Headers.Get<string>("PublishedBy") ?? "unknown";

        var finalRow = row with 
        { 
            Payload = redactedPayload,
            Metadata = JsonSerializer.SerializeToElement(metadataDict)
        };

        // 4. Pass to batched writer
        await _writer.WriteAsync(finalRow, context.CancellationToken);
    }

    private static string GenerateDeterministicHash(string eventType, JsonElement payload, DateTimeOffset occurredAt)
    {
        var input = $"{eventType}|{payload.GetRawText()}|{occurredAt.ToUnixTimeMilliseconds()}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
