namespace Haworks.BuildingBlocks.Persistence;

/// <summary>
/// Marker interface for entities with a GUID primary key.
/// </summary>
public interface IEntityWithGuid
{
    Guid Id { get; set; }
}
