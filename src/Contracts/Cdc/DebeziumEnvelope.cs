using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haworks.Contracts.Cdc;

/// <summary>
/// Debezium change event envelope. Deserialized from Kafka topic values
/// produced by Debezium Connect watching Postgres WAL.
/// </summary>
public sealed record DebeziumEnvelope(
    [property: JsonPropertyName("before")] JsonElement? Before,
    [property: JsonPropertyName("after")]  JsonElement? After,
    [property: JsonPropertyName("op")]     string Op,
    [property: JsonPropertyName("ts_ms")]  long TsMs,
    [property: JsonPropertyName("source")] DebeziumSource? Source);

public sealed record DebeziumSource(
    [property: JsonPropertyName("db")]     string? Db,
    [property: JsonPropertyName("schema")] string? Schema,
    [property: JsonPropertyName("table")]  string? Table,
    [property: JsonPropertyName("txId")]   long? TxId);
