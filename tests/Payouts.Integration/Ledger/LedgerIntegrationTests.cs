using FluentAssertions;
using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Application.Ledger.Services;
using Haworks.Payouts.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haworks.Payouts.Integration.Ledger;

[Collection(nameof(PayoutsIntegrationTestDefinition))]
public class LedgerIntegrationTests : IAsyncLifetime
{
    private readonly PayoutsWebAppFactory _factory;
    private readonly IServiceScope _scope;
    private readonly ILedgerService _ledgerService;
    private readonly IPayoutsDbContext _context;

    public LedgerIntegrationTests(PayoutsWebAppFactory factory)
    {
        _factory = factory;
        _scope = _factory.Services.CreateScope();
        _ledgerService = _scope.ServiceProvider.GetRequiredService<ILedgerService>();
        _context = _scope.ServiceProvider.GetRequiredService<IPayoutsDbContext>();
    }

    public async Task InitializeAsync() => await _factory.EnsureSchemaAsync();
    public Task DisposeAsync() { _scope.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task CreditSellerAsync_Should_Create_Double_Entry_And_Update_Balances()
    {
        var sellerId = Guid.NewGuid();
        var amount = 100.00m;
        var currency = "USD";
        await _ledgerService.CreditSellerAsync(sellerId, amount, currency, Guid.NewGuid(), "Test credit");
        var sellerBalance = await _ledgerService.GetBalanceAsync(sellerId, AccountType.SellerPending, currency);
        sellerBalance.Should().Be(amount);
        var platformId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var platformBalance = await _ledgerService.GetBalanceAsync(platformId, AccountType.PlatformHolding, currency);
        platformBalance.Should().Be(-amount);
    }
}
