using Haworks.BuildingBlocks.Persistence;
namespace Haworks.Content.Domain.Entities;

public class ContentVersion : AuditableEntity
{
    /// <summary>
    /// Protected parameterless constructor for EF Core materialization.
    /// </summary>
    protected ContentVersion() : base() { }

    private ContentVersion(Guid contentId, string versionInfo) : base()
    {
        ContentId = contentId;
        VersionInfo = versionInfo;
    }

    public Guid ContentId { get; private set; }
    public ContentEntity? Content { get; private set; }
    public string VersionInfo { get; private set; } = string.Empty;

    /// <summary>
    /// Creates a new content version.
    /// </summary>
    public static ContentVersion Create(Guid contentId, string versionInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionInfo);
        return new ContentVersion(contentId, versionInfo);
    }

    /// <summary>
    /// Sets the parent content reference.
    /// </summary>
    public void SetContent(ContentEntity content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        ContentId = content.Id;
    }
}
