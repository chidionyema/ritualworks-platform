using Haworks.BuildingBlocks.Persistence;
namespace Haworks.Content.Domain.Entities;

public class ContentEntity : AuditableEntity
{
    private readonly List<ContentMetadata> _metadata = [];
    private readonly List<ContentVersion> _versions = [];

    /// <summary>
    /// Protected parameterless constructor for EF Core materialization.
    /// </summary>
    protected ContentEntity() : base() { }

    private ContentEntity(Guid id, Guid entityId, string entityType, ContentType contentType) : base(id)
    {
        EntityId = entityId;
        EntityType = entityType;
        ContentType = contentType;
    }

    private ContentEntity(Guid entityId, string entityType, ContentType contentType) : base()
    {
        EntityId = entityId;
        EntityType = entityType;
        ContentType = contentType;
    }

    public Guid EntityId { get; private set; }
    public string EntityType { get; private set; } = string.Empty;
    public ContentType ContentType { get; private set; }
    public string BlobName { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string BucketName { get; private set; } = string.Empty;
    public string ObjectName { get; private set; } = string.Empty;
    public string ETag { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    public string StorageDetails { get; private set; } = string.Empty;
    public string Path { get; private set; } = string.Empty;
    public string Url { get; private set; } = string.Empty;
    public IReadOnlyCollection<ContentMetadata> Metadata => _metadata.AsReadOnly();
    public IReadOnlyCollection<ContentVersion> Versions => _versions.AsReadOnly();

    /// <summary>
    /// Creates a new content entity with a specific ID.
    /// </summary>
    public static ContentEntity Create(Guid id, Guid entityId, string entityType, ContentType contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        return new ContentEntity(id, entityId, entityType, contentType);
    }

    /// <summary>
    /// Creates a new content entity with auto-generated ID.
    /// </summary>
    public static ContentEntity Create(Guid entityId, string entityType, ContentType contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        return new ContentEntity(entityId, entityType, contentType);
    }

    /// <summary>
    /// Sets storage information.
    /// </summary>
    public void SetStorageInfo(string bucketName, string objectName, string blobName, long fileSize)
    {
        BucketName = bucketName;
        ObjectName = objectName;
        BlobName = blobName;
        FileSize = fileSize;
    }

    /// <summary>
    /// Sets file information.
    /// </summary>
    public void SetFileInfo(string fileName, string eTag, string slug)
    {
        FileName = fileName;
        ETag = eTag;
        Slug = slug;
    }

    /// <summary>
    /// Sets URL and path information.
    /// </summary>
    public void SetUrlInfo(string url, string path)
    {
        Url = url;
        Path = path;
    }

    /// <summary>
    /// Sets additional storage details (JSON).
    /// </summary>
    public void SetStorageDetails(string storageDetails)
    {
        StorageDetails = storageDetails;
    }

    /// <summary>
    /// Adds metadata to this content.
    /// </summary>
    public void AddMetadata(ContentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata.Add(metadata);
    }

    /// <summary>
    /// Adds a version record.
    /// </summary>
    public void AddVersion(ContentVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        _versions.Add(version);
    }

    /// <summary>
    /// Updates the content type.
    /// </summary>
    public void SetContentType(ContentType contentType)
    {
        ContentType = contentType;
    }
}
