using System.ComponentModel.DataAnnotations;

namespace Haworks.Identity.Domain;

public class RefreshToken : AuditableEntity
{
    /// <summary>
    /// Protected parameterless constructor for EF Core materialization.
    /// </summary>
    protected RefreshToken() : base() { }

    private RefreshToken(string userId, string token, DateTime expires) : base()
    {
        UserId = userId;
        Token = token;
        Expires = expires;
    }

    public string UserId { get; private set; } = string.Empty;
    public User? User { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public DateTime Expires { get; private set; }

    /// <summary>
    /// Creates a new refresh token.
    /// </summary>
    public static RefreshToken Create(string userId, string token, DateTime expires)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return new RefreshToken(userId, token, expires);
    }

    /// <summary>
    /// Checks if the token has expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= Expires;

    /// <summary>
    /// Sets the user reference.
    /// </summary>
    public void SetUser(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        User = user;
    }
}

public class RevokedToken : AuditableEntity
{
    /// <summary>
    /// Protected parameterless constructor for EF Core materialization.
    /// </summary>
    protected RevokedToken() : base() { }

    private RevokedToken(string token, DateTime revokedAt, DateTime expiresAt, string? reason, string? userId) : base()
    {
        Token = token;
        RevokedAt = revokedAt;
        ExpiresAt = expiresAt;
        Reason = reason;
        UserId = userId;
    }

    [Required]
    [MaxLength(500)]
    public string Token { get; private set; } = string.Empty;

    [Required]
    public DateTime RevokedAt { get; private set; }

    [MaxLength(200)]
    public string? Reason { get; private set; }

    [MaxLength(450)]
    public string? UserId { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    public virtual User? User { get; private set; }

    /// <summary>
    /// Creates a new revoked token record.
    /// </summary>
    public static RevokedToken Create(string token, DateTime expiresAt, string? reason = null, string? userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return new RevokedToken(token, DateTime.UtcNow, expiresAt, reason, userId);
    }

    /// <summary>
    /// Checks if the revoked token record has expired (can be cleaned up).
    /// </summary>
    public bool CanBeCleanedUp => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Sets the user reference.
    /// </summary>
    public void SetUser(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        User = user;
        UserId = user.Id;
    }
}
