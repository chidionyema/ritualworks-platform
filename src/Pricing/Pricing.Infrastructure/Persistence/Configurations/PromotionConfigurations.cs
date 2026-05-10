using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Haworks.Pricing.Domain.Aggregates;

namespace Haworks.Pricing.Infrastructure.Persistence.Configurations;

public class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.ToTable("promotions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.DiscountType).HasConversion<string>();
        
        // Map AuditableEntity properties if needed, or EF handles it if configured conventionally.
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasMany(x => x.Rules)
               .WithOne()
               .HasForeignKey(x => x.PromotionId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Redemptions)
               .WithOne()
               .HasForeignKey(x => x.PromotionId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

public class PromotionRuleConfiguration : IEntityTypeConfiguration<PromotionRule>
{
    public void Configure(EntityTypeBuilder<PromotionRule> builder)
    {
        builder.ToTable("promotion_rules");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RuleType).HasConversion<string>();
        builder.Property(x => x.TargetValue).IsRequired().HasMaxLength(500);
        
        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}

public class PromotionRedemptionConfiguration : IEntityTypeConfiguration<PromotionRedemption>
{
    public void Configure(EntityTypeBuilder<PromotionRedemption> builder)
    {
        builder.ToTable("promotion_redemptions");
        builder.HasKey(x => x.Id);
        
        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
