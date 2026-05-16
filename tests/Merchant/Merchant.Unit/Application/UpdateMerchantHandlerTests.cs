using FluentAssertions;
using Haworks.Merchant.Application.Common.Interfaces;
using Haworks.Merchant.Application.Merchants.Commands.UpdateMerchant;
using Haworks.Merchant.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Haworks.Merchant.Unit.Application;

public sealed class UpdateMerchantHandlerTests
{
    [Fact]
    public async Task Handle_nonexistent_merchant_returns_not_found()
    {
        var context = CreateInMemoryContext();
        var handler = new UpdateMerchantCommandHandler(context);

        var command = new UpdateMerchantCommand(Guid.NewGuid(), Guid.NewGuid(), "Name", null, null, null, null, null, null, null);
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Merchant.NotFound");
    }

    [Fact]
    public async Task Handle_wrong_owner_returns_forbidden()
    {
        var context = CreateInMemoryContext();
        var ownerId = Guid.NewGuid();
        var merchant = MerchantProfile.Create(ownerId, "Test", "test");
        context.Merchants.Add(merchant);
        await context.SaveChangesAsync();

        var handler = new UpdateMerchantCommandHandler(context);
        var differentUserId = Guid.NewGuid();
        var command = new UpdateMerchantCommand(merchant.Id, differentUserId, "New Name", null, null, null, null, null, null, null);
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Merchant.Forbidden");
    }

    [Fact]
    public async Task Handle_valid_owner_updates_successfully()
    {
        var context = CreateInMemoryContext();
        var ownerId = Guid.NewGuid();
        var merchant = MerchantProfile.Create(ownerId, "Test", "test");
        context.Merchants.Add(merchant);
        await context.SaveChangesAsync();

        var handler = new UpdateMerchantCommandHandler(context);
        var command = new UpdateMerchantCommand(merchant.Id, ownerId, "Updated", "New Bio", null, null, null, null, null, null);
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var updated = await context.Merchants.FirstAsync(m => m.Id == merchant.Id);
        updated.Name.Should().Be("Updated");
        updated.Bio.Should().Be("New Bio");
    }

    private static TestMerchantDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<TestMerchantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestMerchantDbContext(options);
    }
}

internal sealed class TestMerchantDbContext : DbContext, IMerchantDbContext
{
    public TestMerchantDbContext(DbContextOptions<TestMerchantDbContext> options) : base(options) { }
    public DbSet<MerchantProfile> Merchants => Set<MerchantProfile>();
    public DbSet<OperatingHours> OperatingHours => Set<OperatingHours>();
}
