using FluentAssertions;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using Xunit;

namespace Haworks.Payouts.Unit.Domain;

public class LedgerAccountTests
{
    [Fact]
    public void Create_sets_zero_balance()
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), AccountType.SellerPending, "USD");
        account.Balance.Should().Be(0);
    }

    [Fact]
    public void Credit_increases_balance()
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), AccountType.SellerPending, "USD");
        account.UpdateBalance(100m, EntryType.Credit);
        account.Balance.Should().Be(100m);
    }

    [Fact]
    public void Debit_decreases_balance()
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), AccountType.SellerPending, "USD");
        account.UpdateBalance(100m, EntryType.Credit);
        account.UpdateBalance(40m, EntryType.Debit);
        account.Balance.Should().Be(60m);
    }

    [Fact]
    public void Debit_exceeding_seller_balance_throws()
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), AccountType.SellerPending, "USD");
        account.UpdateBalance(50m, EntryType.Credit);
        var act = () => account.UpdateBalance(51m, EntryType.Debit);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Insufficient*");
    }

    [Fact]
    public void Debit_on_platform_account_requires_sufficient_balance()
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), AccountType.PlatformHolding, "USD");
        account.UpdateBalance(100m, EntryType.Credit);
        account.UpdateBalance(50m, EntryType.Debit);
        account.Balance.Should().Be(50m);
    }
}
