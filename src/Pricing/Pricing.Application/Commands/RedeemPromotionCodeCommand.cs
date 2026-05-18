using Haworks.BuildingBlocks.Idempotency;
using MediatR;

namespace Haworks.Pricing.Application.Commands;

/// <summary>
/// Redeems a promotion code atomically using pessimistic locking (FOR UPDATE + CAS).
/// </summary>
public sealed record RedeemPromotionCodeCommand : IIdempotentCommand, IRequest<RedeemPromotionCodeResult>
{
    public required string Code { get; init; }
    public required Guid OrderId { get; init; }
    public string? UserId { get; init; }
    public required decimal DiscountAmount { get; init; }
    public required Guid CalculationId { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
}

/// <summary>
/// Result of a promotion code redemption attempt.
/// </summary>
public sealed record RedeemPromotionCodeResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
}
