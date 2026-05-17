using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Entities;
using Haworks.Pricing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Pricing.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of IPromotionCodeRepository with atomic CAS redemption.
/// </summary>
public sealed class PromotionCodeRepository : IPromotionCodeRepository
{
    private readonly PricingDbContext _context;

    public PromotionCodeRepository(PricingDbContext context)
    {
        _context = context;
    }

    public Task<PromotionCode?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        return _context.PromotionCodes
            .IgnoreQueryFilters()
            .Include(c => c.Redemptions)
            .FirstOrDefaultAsync(c => c.Code == code, ct);
    }

    public Task<PromotionCode?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return _context.PromotionCodes
            .Include(c => c.Redemptions)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<IReadOnlyList<PromotionCode>> GetAllPagedAsync(int page, int pageSize, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        return await _context.PromotionCodes
            .IgnoreQueryFilters()
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(PromotionCode code, CancellationToken ct = default)
    {
        await _context.PromotionCodes.AddAsync(code, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomic CAS redemption using ExecuteSqlRawAsync.
    /// Returns true if the redemption succeeded.
    /// </summary>
    public async Task<bool> TryRedeemAsync(Guid promotionCodeId, Guid orderId, string? userId, decimal discountAmount, CancellationToken ct = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            // Set lock timeout for this transaction
            await _context.Database.ExecuteSqlRawAsync(
                "SET LOCAL lock_timeout = '500ms'", ct).ConfigureAwait(false);

            // Atomic CAS UPDATE — increment UsesCount only if under limit
            var rowsAffected = await _context.Database.ExecuteSqlRawAsync(
                """
                UPDATE pricing."PromotionCodes"
                SET "UsesCount" = "UsesCount" + 1, "LastModifiedDate" = NOW()
                WHERE "Id" = {0}
                  AND ("MaxUses" IS NULL OR "UsesCount" < "MaxUses")
                """,
                new object[] { promotionCodeId },
                ct).ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            // Check if this order already redeemed (idempotency)
            var existingRedemption = await _context.PromotionRedemptions
                .AnyAsync(r => r.PromotionCodeId == promotionCodeId && r.OrderId == orderId, ct)
                .ConfigureAwait(false);

            if (existingRedemption)
            {
                // Idempotent — already redeemed for this order, rollback the increment
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return true; // Still success from caller's perspective
            }

            // Insert redemption record
            var redemption = PromotionRedemption.Create(promotionCodeId, orderId, userId, discountAmount);
            await _context.PromotionRedemptions.AddAsync(redemption, ct).ConfigureAwait(false);
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }
    }

    public Task<int> GetUserRedemptionCountAsync(Guid promotionCodeId, string userId, CancellationToken ct = default)
    {
        return _context.PromotionRedemptions
            .CountAsync(r => r.PromotionCodeId == promotionCodeId && r.UserId == userId, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _context.SaveChangesAsync(ct);
    }
}
