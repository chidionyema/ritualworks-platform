namespace Haworks.Catalog.Domain;

/// <summary>
/// A user-submitted review for a Product. <see cref="UserId"/> is an opaque
/// foreign key to identity-svc — no FK constraint across the service
/// boundary (ADR-0009). Author display name is denormalized at write time
/// from a UserProfileChanged event subscription (Phase 2c+).
/// </summary>
public class ProductReview : AuditableEntity
{
    /// <summary>EF Core materialization constructor.</summary>
    protected ProductReview() : base() { }

    private ProductReview(Guid productId, string userId, int rating, string? body, string? authorName, string? title) : base()
    {
        ProductId = productId;
        UserId = userId;
        Rating = rating;
        Body = body;
        AuthorName = authorName;
        Title = title;
        IsApproved = false;
    }

    public Guid ProductId { get; private set; }
    public Product? Product { get; private set; }
    public string UserId { get; private set; } = string.Empty;  // opaque FK to identity
    public string? AuthorName { get; private set; }
    public string? Title { get; private set; }
    public int Rating { get; private set; }
    public string? Body { get; private set; }
    public bool IsApproved { get; private set; }

    public static ProductReview Create(Guid productId, string userId, int rating, string? body, string? authorName = null, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (rating is < 1 or > 5)
            throw new ArgumentException("Rating must be 1–5", nameof(rating));
        return new ProductReview(productId, userId, rating, body, authorName, title);
    }

    public void Approve() { IsApproved = true; LastModifiedDate = DateTime.UtcNow; }
    public void Reject()  { IsApproved = false; LastModifiedDate = DateTime.UtcNow; }
    
    public void Update(int rating, string? body, string? title)
    {
        if (rating is < 1 or > 5)
            throw new ArgumentException("Rating must be 1–5", nameof(rating));
        
        Rating = rating;
        Body = body;
        Title = title;
        LastModifiedDate = DateTime.UtcNow;
    }
}
