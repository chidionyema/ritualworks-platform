namespace Haworks.Cdc.Application.Models;

public sealed class ReplicationOptions
{
    public required string ConnectionString { get; init; }
    public required string SlotName { get; init; }
    public required string PublicationName { get; init; }
    public required string SourceService { get; init; }
}
