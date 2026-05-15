namespace Haworks.Media.Api.Domain;

public enum MediaStatus
{
    Pending,
    Quarantined,
    Active,
    Rejected
}

public class MediaFile
{
    public Guid Id { get; private set; }
    public string FileName { get; private set; } = null!;
    public string Hash { get; private set; } = null!;
    public long Size { get; private set; }
    public string MimeType { get; private set; } = null!;
    public MediaStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private MediaFile() { }

    public static MediaFile Create(string fileName, string hash, long size, string mimeType)
    {
        return new MediaFile
        {
            Id = Guid.NewGuid(),
            FileName = fileName,
            Hash = hash,
            Size = size,
            MimeType = mimeType,
            Status = MediaStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void MarkAsQuarantined() => Status = MediaStatus.Quarantined;
    public void MarkAsActive() => Status = MediaStatus.Active;
    public void MarkAsRejected() => Status = MediaStatus.Rejected;
}
