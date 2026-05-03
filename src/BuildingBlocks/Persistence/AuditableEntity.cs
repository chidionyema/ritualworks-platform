namespace Haworks.BuildingBlocks.Persistence;

/// <summary>
/// Base class for entities with audit metadata.
/// All aggregate roots in services should inherit from this.
/// </summary>
public abstract class AuditableEntity : IEntityWithGuid
{
    protected AuditableEntity()
    {
        Id = Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
        RowVersion = new byte[8];
    }

    protected AuditableEntity(Guid id)
    {
        Id = id;
        CreatedAt = DateTime.UtcNow;
        RowVersion = new byte[8];
    }

    public Guid Id { get; set; }
    public string? CreatedFromIp { get; set; }
    public string? ModifiedFromIp { get; set; }
    public byte[] RowVersion { get; set; } = null!;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? LastModifiedBy { get; set; }
    public DateTime? LastModifiedDate { get; set; }
}
