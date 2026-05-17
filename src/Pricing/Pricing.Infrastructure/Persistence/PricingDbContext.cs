using Haworks.Pricing.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Pricing.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext for the Pricing service.
/// </summary>
public sealed class PricingDbContext : DbContext
{
    public PricingDbContext(DbContextOptions<PricingDbContext> options) : base(options) { }

    public DbSet<PriceRule> PriceRules => Set<PriceRule>();
    public DbSet<TieredPrice> TieredPrices => Set<TieredPrice>();
    public DbSet<PromotionCode> PromotionCodes => Set<PromotionCode>();
    public DbSet<PromotionRedemption> PromotionRedemptions => Set<PromotionRedemption>();
    public DbSet<TaxRate> TaxRates => Set<TaxRate>();
    public DbSet<PriceCalculationLog> CalculationLogs => Set<PriceCalculationLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("pricing");

        // PriceRule
        modelBuilder.Entity<PriceRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DiscountValue).HasColumnType("numeric(18,4)");
            e.Property(x => x.SellerTimezone).HasMaxLength(64);
            e.Property(x => x.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");
            e.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            e.HasMany(x => x.TieredPrices).WithOne().HasForeignKey(t => t.PriceRuleId);
            e.HasIndex(x => new { x.ProductId, x.IsActive, x.IsDeleted });
            e.HasIndex(x => new { x.CategoryId, x.IsActive, x.IsDeleted });
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // TieredPrice
        modelBuilder.Entity<TieredPrice>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UnitPrice).HasColumnType("numeric(18,4)");
            e.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        // PromotionCode
        modelBuilder.Entity<PromotionCode>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.DiscountValue).HasColumnType("numeric(18,4)");
            e.Property(x => x.MinimumOrderAmount).HasColumnType("numeric(18,4)");
            e.Property(x => x.SellerTimezone).HasMaxLength(64);
            e.Property(x => x.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");
            e.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Redemptions).WithOne().HasForeignKey(r => r.PromotionCodeId);
            e.HasQueryFilter(x => !x.IsDeleted);
            e.ToTable(t => t.HasCheckConstraint("CK_PromotionCodes_UsesCount", "\"UsesCount\" >= 0"));
        });

        // PromotionRedemption
        modelBuilder.Entity<PromotionRedemption>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DiscountAmountApplied).HasColumnType("numeric(18,4)");
            e.Property(x => x.UserId).HasMaxLength(256);
            e.HasIndex(x => new { x.PromotionCodeId, x.OrderId }).IsUnique();
            e.HasIndex(x => new { x.PromotionCodeId, x.UserId });
        });

        // TaxRate
        modelBuilder.Entity<TaxRate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.StateCode).HasMaxLength(3);
            e.Property(x => x.CombinedRate).HasColumnType("numeric(8,6)");
            e.Property(x => x.StateRate).HasColumnType("numeric(8,6)");
            e.Property(x => x.CountyRate).HasColumnType("numeric(8,6)");
            e.Property(x => x.LocalRate).HasColumnType("numeric(8,6)");
            e.Property(x => x.Notes).HasMaxLength(512);
            e.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            e.HasIndex(x => new { x.CountryCode, x.StateCode, x.EffectiveFrom });
        });

        // PriceCalculationLog (append-only)
        modelBuilder.Entity<PriceCalculationLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.BaseUnitPrice).HasColumnType("numeric(18,4)");
            e.Property(x => x.EffectiveUnitPrice).HasColumnType("numeric(18,4)");
            e.Property(x => x.Subtotal).HasColumnType("numeric(18,4)");
            e.Property(x => x.TaxAmount).HasColumnType("numeric(18,4)");
            e.Property(x => x.TaxRateApplied).HasColumnType("numeric(8,6)");
            e.Property(x => x.Total).HasColumnType("numeric(18,4)");
            e.Property(x => x.SnapshotProductPrice).HasColumnType("numeric(18,4)");
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.StateCode).HasMaxLength(3);
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.PromotionCodeApplied).HasMaxLength(32);
            e.HasIndex(x => x.CalculatedAt);
            e.HasIndex(x => x.ProductId);
            // M6 Fix: Index for user-scoped audit queries (support, compliance)
            e.HasIndex(x => x.UserId);
        });
    }
}
