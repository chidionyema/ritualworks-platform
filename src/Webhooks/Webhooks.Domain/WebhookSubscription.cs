using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Webhooks.Domain;

public sealed class WebhookSubscription : AuditableEntity
{
    public Guid PartnerId { get; private set; }
    public string Url { get; private set; } = null!;
    public string Secret { get; private set; } = null!; // Plain secret for HMAC signing
    public string SecretHash { get; private set; } = null!; // bcrypt of secret for validation
    public string SecretPreview { get; private set; } = null!;
    public string[] Events { get; private set; } = [];
    public bool IsActive { get; private set; } = true;
    public DateTime? DeletedAt { get; private set; }

    private WebhookSubscription() { } // EF

    public WebhookSubscription(
        Guid partnerId, 
        string url, 
        string secret,
        string secretHash, 
        string secretPreview, 
        string[] events,
        bool isActive = true)
    {
        Id = Guid.NewGuid();
        PartnerId = partnerId;
        Url = url;
        Secret = secret;
        SecretHash = secretHash;
        SecretPreview = secretPreview;
        Events = events;
        IsActive = isActive;
        CreatedAt = DateTime.UtcNow;
        LastModifiedDate = DateTime.UtcNow;
    }

    public void Update(string url, string[] events, bool isActive)
    {
        Url = url;
        Events = events;
        IsActive = isActive;
        LastModifiedDate = DateTime.UtcNow;
    }

    public void RotateSecret(string secret, string secretHash, string secretPreview)
    {
        Secret = secret;
        SecretHash = secretHash;
        SecretPreview = secretPreview;
        LastModifiedDate = DateTime.UtcNow;
    }

    public void SoftDelete()
    {
        DeletedAt = DateTime.UtcNow;
        LastModifiedDate = DateTime.UtcNow;
    }
}
