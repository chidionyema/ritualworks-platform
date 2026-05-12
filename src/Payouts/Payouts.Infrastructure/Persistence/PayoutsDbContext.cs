using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Application.Common.Interfaces;
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

        builder.Entity<LedgerAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OwnerId, e.Type, e.Currency }).IsUnique();
        });

        builder.Entity<LedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AccountId);
            entity.HasIndex(e => e.TransactionId);
        });

        builder.Entity<Payout>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SellerId);
        });

        builder.Entity<SellerProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SellerId).IsUnique();
        });
    }
}
