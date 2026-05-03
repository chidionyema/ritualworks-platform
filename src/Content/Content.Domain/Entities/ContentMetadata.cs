using Haworks.BuildingBlocks.Persistence;
namespace Haworks.Content.Domain.Entities;

public class ContentMetadata
{
    /// <summary>
    /// Protected parameterless constructor for EF Core materialization.
    /// </summary>
    protected ContentMetadata() { }

    private ContentMetadata(Guid contentId, string key, string value)
    {
        Id = Guid.NewGuid();
        ContentId = contentId;
        Key = key;
        Value = value;
    }

    public Guid Id { get; private set; }
    public Guid ContentId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public ContentEntity? Content { get; private set; }

    /// <summary>
    /// Creates a new content metadata entry.
    /// </summary>
    public static ContentMetadata Create(Guid contentId, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new ContentMetadata(contentId, key, value);
    }

    /// <summary>
    /// Updates the metadata value.
    /// </summary>
    public void UpdateValue(string newValue)
    {
        Value = newValue;
    }
}
