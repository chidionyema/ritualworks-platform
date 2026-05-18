using FluentAssertions;
using Haworks.Payouts.Application.Ledger.Services;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using Haworks.Payouts.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Haworks.Payouts.Unit.Ledger;

[Trait("Category", "Integration")]
public sealed class LedgerServiceTests : IDisposable
{
    private readonly PayoutsDbContext _context;
    private readonly LedgerService _service;

    public LedgerServiceTests()
    {
        var options = new DbContextOptionsBuilder<PayoutsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _context = new PayoutsDbContext(options);
        _service = new LedgerService(_context, NullLogger<LedgerService>.Instance);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task CreditSellerAsync_creates_three_entries()
    {
        var sellerId = Guid.NewGuid();
        var profile = SellerProfile.Create(sellerId);
        profile.CommissionPercentage = 10m;
        _context.SellerProfiles.Add(profile);
        await _context.SaveChangesAsync();

        await _service.CreditSellerAsync(sellerId, 100m, "USD", Guid.NewGuid(), "Test payment");

        var entries = await _context.LedgerEntries.ToListAsync();
        entries.Should().HaveCount(3);

        var sellerBalance = await _service.GetBalanceAsync(sellerId, AccountType.SellerPending, "USD");
        sellerBalance.Should().Be(90m); // 100 - 10% commission
    }

    [Fact]
    public async Task CreditSellerAsync_is_idempotent_on_same_referenceId()
    {
        var sellerId = Guid.NewGuid();
        var refId = Guid.NewGuid();

        await _service.CreditSellerAsync(sellerId, 100m, "USD", refId, "Payment 1");
        await _service.CreditSellerAsync(sellerId, 100m, "USD", refId, "Payment 1 duplicate");

        var entries = await _context.LedgerEntries.ToListAsync();
        entries.Should().HaveCount(3); // Only first credit, not 6

        var sellerBalance = await _service.GetBalanceAsync(sellerId, AccountType.SellerPending, "USD");
        sellerBalance.Should().Be(90m); // Not 180
    }

    [Fact]
    public Task CreditSellerAsync_with_zero_amount_throws()
    {
        var act = () => _service.CreditSellerAsync(Guid.NewGuid(), 0m, "USD", Guid.NewGuid(), "test");
        return act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DebitSellerAsync_reverses_credit()
    {
        var sellerId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        await _service.CreditSellerAsync(sellerId, 100m, "USD", paymentId, "Payment");

        await _service.DebitSellerAsync(sellerId, 100m, "USD", paymentId, "Refund");

        var sellerBalance = await _service.GetBalanceAsync(sellerId, AccountType.SellerPending, "USD");
        sellerBalance.Should().Be(0m);
    }

    [Fact]
    public async Task DebitSellerAsync_is_idempotent()
    {
        var sellerId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        await _service.CreditSellerAsync(sellerId, 100m, "USD", paymentId, "Payment");

        await _service.DebitSellerAsync(sellerId, 100m, "USD", paymentId, "Refund");
        await _service.DebitSellerAsync(sellerId, 100m, "USD", paymentId, "Refund duplicate");

        var entries = await _context.LedgerEntries.CountAsync();
        entries.Should().Be(6); // 3 credit + 3 debit (not 9)
    }

    [Fact]
    public async Task GetBalanceAsync_returns_zero_for_nonexistent_account()
    {
        var balance = await _service.GetBalanceAsync(Guid.NewGuid(), AccountType.SellerPending, "USD");
        balance.Should().Be(0);
    }
}
