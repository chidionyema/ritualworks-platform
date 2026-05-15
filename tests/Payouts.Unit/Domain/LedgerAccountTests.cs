using FluentAssertions;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using Xunit;

namespace Haworks.Payouts.Unit.Domain;

public class LedgerAccountTests
{
    [Fact]
    public void UpdateBalance_WithCredit_ShouldIncreaseBalance()
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), AccountType.SellerPending, "USD");
        account.UpdateBalance(100.50m, EntryType.Credit);
        account.Balance.Should().Be(100.50m);
    }

    [Fact]
    public void UpdateBalance_WithDebit_ShouldDecreaseBalance()
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), AccountType.SellerPayable, "USD");
        account.UpdateBalance(200m, EntryType.Credit);
        account.UpdateBalance(50m, EntryType.Debit);
        account.Balance.Should().Be(150m);
    }

    [Fact]
    public void LedgerAccount_debit_exceeding_balance_throws()
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), AccountType.SellerPending, "USD");
        account.UpdateBalance(50m, EntryType.Credit);

        var act = () => account.UpdateBalance(100m, EntryType.Debit);
        act.Should().Throw<InvalidOperationException>().WithMessage("Insufficient balance");
    }

    [Fact]
    public void Payout_Create_with_zero_amount_throws()
    {
        var act = () => Payout.Create(Guid.NewGuid(), 0m, "USD");
        act.Should().Throw<ArgumentException>().WithMessage("Payout amount must be positive");
    }
}
