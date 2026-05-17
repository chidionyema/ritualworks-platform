using System.Text.Json.Serialization;

namespace Haworks.Contracts.Cdc;

public sealed record DebeziumSource(
    [property: JsonPropertyName("db")]     string? Db,
    [property: JsonPropertyName("schema")] string? Schema,
    [property: JsonPropertyName("table")]  string? Table,
    [property: JsonPropertyName("txId")]   long? TxId);
