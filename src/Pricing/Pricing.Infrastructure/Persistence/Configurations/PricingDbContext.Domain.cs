using Microsoft.EntityFrameworkCore;
using Haworks.Pricing.Domain.Aggregates;

namespace Haworks.Pricing.Infrastructure.Persistence;

public partial class PricingDbContext
{
    public DbSet<Promotion> Promotions { get; set; } = null!;
    public DbSet<PromotionRule> PromotionRules { get; set; } = null!;
    public DbSet<PromotionRedemption> PromotionRedemptions { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PricingDbContext).Assembly);
    }
}
