using Haworks.Pricing.Domain.Entities;

namespace Haworks.Pricing.Application.Interfaces;

/// <summary>
/// Repository for PromotionCode aggregate including atomic redemption.
/// </summary>
public interface IPromotionCodeRepository
{
    Task<PromotionCode?> GetByCodeAsync(string code, CancellationToken ct = default);
    Task<PromotionCode?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<PromotionCode>> GetAllPagedAsync(int page, int pageSize, CancellationToken ct = default);
    Task AddAsync(PromotionCode code, CancellationToken ct = default);

    /// <summary>
    /// Atomic CAS redemption. Returns true if redemption succeeded, false if exhausted.
    /// Uses ExecuteSqlRawAsync with FOR UPDATE + compare-and-swap.
    /// </summary>
    Task<bool> TryRedeemAsync(Guid promotionCodeId, Guid orderId, string? userId, decimal discountAmount, CancellationToken ct = default);

    /// <summary>
    /// Check per-user redemption count.
    /// </summary>
    Task<int> GetUserRedemptionCountAsync(Guid promotionCodeId, string userId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
