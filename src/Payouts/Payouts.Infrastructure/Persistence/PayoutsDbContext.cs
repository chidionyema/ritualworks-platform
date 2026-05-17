using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Application.Common.Interfaces;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payouts.Infrastructure.Persistence;

public class PayoutsDbContext : DbContext, IPayoutsDbContext
{
    public PayoutsDbContext(DbContextOptions<PayoutsDbContext> options) : base(options) { }

    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<SellerProfile> SellerProfiles => Set<SellerProfile>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("payouts");

        builder.AddInboxStateEntity();
        builder.AddOutboxStateEntity();
        builder.AddOutboxMessageEntity();

        builder.Entity<LedgerAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OwnerId, e.Type, e.Currency }).IsUnique();
            entity.Property(e => e.Balance).HasColumnType("numeric(18,2)");
            entity.Property(e => e.Currency).HasMaxLength(3);
            entity.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        builder.Entity<LedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AccountId);
            entity.HasIndex(e => e.TransactionId);
            entity.HasIndex(e => e.ReferenceId);
            entity.Property(e => e.Amount).HasColumnType("numeric(18,2)");
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ReferenceId).HasMaxLength(255);
        });

        builder.Entity<Payout>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SellerId);
            entity.Property(e => e.Amount).HasColumnType("numeric(18,2)");
            entity.Property(e => e.Currency).HasMaxLength(3);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.ExternalReference).HasMaxLength(500);
            entity.Property(e => e.TransitReference).HasMaxLength(500);
            entity.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        builder.Entity<SellerProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SellerId).IsUnique();
            entity.Property(e => e.CommissionPercentage).HasColumnType("numeric(5,2)");
            entity.Property(e => e.PayoutThreshold).HasColumnType("numeric(18,2)");
            entity.Property(e => e.ExternalProviderId).HasMaxLength(255);
            entity.Property(e => e.KycStatus).HasMaxLength(50);
            entity.Property(e => e.PayoutSchedule).HasMaxLength(20);
        });
    }
}
