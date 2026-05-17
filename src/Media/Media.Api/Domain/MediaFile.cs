namespace Haworks.Media.Api.Domain;

public enum MediaStatus
{
    Pending,
    Quarantined,
    Active,
    Rejected
}

public enum UploadKind
{
    SinglePart,
    Multipart
}

public class MediaFile
{
    public Guid Id { get; private set; }
    public string FileName { get; private set; } = null!;
    public string Hash { get; private set; } = null!;
    public long Size { get; private set; }
    public string MimeType { get; private set; } = null!;
    public MediaStatus Status { get; private set; }
    public string OwnerId { get; private set; } = null!;
    public DateTime CreatedAt { get; private set; }
    public UploadKind UploadKind { get; private set; }
    public string? S3UploadId { get; private set; }
    public int PartCount { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public string? UpdatedBy { get; private set; }

    private MediaFile() { }

    public static MediaFile Create(
        string fileName, string hash, long size, string mimeType, string ownerId,
        TimeProvider? timeProvider = null)
    {
        return new MediaFile
        {
            Id = Guid.NewGuid(),
            FileName = SanitizeFileName(fileName),
            Hash = hash,
            Size = size,
            MimeType = mimeType,
            Status = MediaStatus.Pending,
            OwnerId = ownerId,
            CreatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime,
            UploadKind = UploadKind.SinglePart,
        };
    }

    public void InitiateMultipart(string s3UploadId, int partCount)
    {
        if (Status != MediaStatus.Pending)
            throw new InvalidOperationException($"Cannot initiate multipart from {Status}.");
        S3UploadId = s3UploadId ?? throw new ArgumentNullException(nameof(s3UploadId));
        PartCount = partCount > 0 ? partCount : throw new ArgumentOutOfRangeException(nameof(partCount));
        UploadKind = UploadKind.Multipart;
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = Path.GetFileName(name) ?? name;
        sanitized = new string(sanitized.Where(c => !char.IsControl(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }

    public void MarkAsQuarantined(TimeProvider? time = null, string? actor = null)
    {
        if (Status != MediaStatus.Pending)
            throw new InvalidOperationException($"Cannot quarantine from {Status}; only Pending files can be quarantined.");
        Status = MediaStatus.Quarantined;
        StampUpdate(time, actor);
    }

    public void MarkAsActive(TimeProvider? time = null, string? actor = null)
    {
        if (Status != MediaStatus.Quarantined)
            throw new InvalidOperationException($"Cannot activate from {Status}; only Quarantined (scanned) files can be activated.");
        Status = MediaStatus.Active;
        StampUpdate(time, actor);
    }

    public void MarkAsRejected(TimeProvider? time = null, string? actor = null)
    {
        if (Status != MediaStatus.Quarantined)
            throw new InvalidOperationException($"Cannot reject from {Status}; only Quarantined (scanned) files can be rejected.");
        Status = MediaStatus.Rejected;
        StampUpdate(time, actor);
    }

    public void MarkDeleted(TimeProvider time)
    {
        IsDeleted = true;
        DeletedAt = time.GetUtcNow();
        StampUpdate(time, null);
    }

    private void StampUpdate(TimeProvider? time, string? actor)
    {
        UpdatedAt = (time ?? TimeProvider.System).GetUtcNow();
        if (actor is not null)
            UpdatedBy = actor;
    }
}
