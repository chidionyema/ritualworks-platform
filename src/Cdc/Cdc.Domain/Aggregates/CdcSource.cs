using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Cdc.Domain.Aggregates;

public sealed class CdcSource : AuditableEntity
{
    public required string ServiceName { get; init; }
    public required string ConnectionString { get; set; }
    public string PublicationName { get; set; } = "cdc_publication";
    public string SlotName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? StartedAt { get; set; }

    public static CdcSource Create(string serviceName, string connectionString, string slotName)
    {
        return new CdcSource
        {
            Id = Guid.NewGuid(),
            ServiceName = serviceName,
            ConnectionString = connectionString,
            SlotName = slotName,
            Enabled = true
        };
    }
}
