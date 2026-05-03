namespace Haworks.Contracts.Identity;

/// <summary>
/// Published by identity-svc whenever a user's profile attributes change
/// (registration, profile update, role change, email verification).
/// Other services maintain a local read-model snapshot of users so they
/// can render names/emails without a synchronous gRPC call to identity-svc.
///
/// Per ADR-0009 (DB-per-service, opaque UserId): consumers MUST treat
/// <see cref="UserId"/> as an opaque foreign key. There is no DB-level
/// FK across service boundaries.
///
/// Versioning: additive only within v1. Breaking change = new event
/// type (e.g. UserProfileChangedV2) per the contract evolution rules
/// in 04-testing-strategy.md.
/// </summary>
public sealed record UserProfileChangedEvent : DomainEvent
{
    /// <summary>The user whose profile changed. Opaque foreign key.</summary>
    public required string UserId { get; init; }

    /// <summary>Current email. Always populated (may equal previous if unchanged).</summary>
    public required string Email { get; init; }

    /// <summary>Current display name (UserName in Identity terms).</summary>
    public required string UserName { get; init; }

    /// <summary>Current role list. Empty array if no roles assigned.</summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>
    /// Reason the event was emitted. Helps consumers decide whether they
    /// care: a "Registered" event may trigger welcome emails; "EmailChanged"
    /// might invalidate cached billing info; "RoleAdded" may grant access.
    /// </summary>
    public required string ChangeReason { get; init; }
}
