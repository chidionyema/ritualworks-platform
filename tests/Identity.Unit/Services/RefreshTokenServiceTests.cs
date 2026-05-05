using FluentAssertions;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Testing;
using Haworks.Identity.Application.Options;
using Haworks.Identity.Domain;
using Haworks.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Identity.Unit.Services;

public sealed class RefreshTokenServiceTests : TestBase
{
    private readonly JwtOptions _jwtOptions;

    public RefreshTokenServiceTests(ITestOutputHelper output) : base(output)
    {
        _jwtOptions = new JwtOptions
        {
            Key = "test-key-must-be-at-least-32-characters-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience",
            RefreshTokenExpiryDays = 7
        };
    }

    #region GenerateRefreshTokenAsync Tests

    [Fact]
    public async Task GenerateRefreshTokenAsync_WithValidUserId_ReturnsRefreshToken()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();

        // Act
        var result = await service.GenerateRefreshTokenAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(userId);
        result.Token.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateRefreshTokenAsync_GeneratesUniqueTokens()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();

        // Act
        var token1 = await service.GenerateRefreshTokenAsync(userId);
        var token2 = await service.GenerateRefreshTokenAsync(userId);

        // Assert
        token1.Token.Should().NotBe(token2.Token);
    }

    [Fact]
    public async Task GenerateRefreshTokenAsync_SetsCorrectExpiry()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();
        var beforeCreation = DateTime.UtcNow;

        // Act
        var result = await service.GenerateRefreshTokenAsync(userId);

        // Assert
        var afterCreation = DateTime.UtcNow;
        var expectedExpiryMin = beforeCreation.AddDays(7);
        var expectedExpiryMax = afterCreation.AddDays(7);

        result.Expires.Should().BeOnOrAfter(expectedExpiryMin).And.BeOnOrBefore(expectedExpiryMax);
    }

    [Fact]
    public async Task GenerateRefreshTokenAsync_SetsCreatedAt()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();
        var beforeCreation = DateTime.UtcNow;

        // Act
        var result = await service.GenerateRefreshTokenAsync(userId);

        // Assert
        var afterCreation = DateTime.UtcNow;
        // RefreshToken inherits from AuditableEntity; ensure it tracks CreatedAt
        result.CreatedAt.Should().BeOnOrAfter(beforeCreation).And.BeOnOrBefore(afterCreation);
    }

    [Fact]
    public async Task GenerateRefreshTokenAsync_PersistsToDatabase()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();

        // Act
        var result = await service.GenerateRefreshTokenAsync(userId);

        // Assert
        var persistedToken = await context.RefreshTokens.FirstOrDefaultAsync(rt => rt.Token == result.Token);
        persistedToken.Should().NotBeNull();
        persistedToken!.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task GenerateRefreshTokenAsync_UsesConfiguredExpiryDays()
    {
        // Arrange
        var customOptions = new JwtOptions
        {
            Key = "test-key-must-be-at-least-32-characters-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience",
            RefreshTokenExpiryDays = 30
        };

        await using var context = CreateInMemoryContext();
        var service = CreateService(context, customOptions);
        var userId = Guid.NewGuid().ToString();
        var beforeCreation = DateTime.UtcNow;

        // Act
        var result = await service.GenerateRefreshTokenAsync(userId);

        // Assert
        var expectedExpiry = beforeCreation.AddDays(30);
        result.Expires.Should().BeCloseTo(expectedExpiry, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GenerateRefreshTokenAsync_TokenIs64BytesBase64Encoded()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();

        // Act
        var result = await service.GenerateRefreshTokenAsync(userId);

        // Assert
        var tokenBytes = Convert.FromBase64String(result.Token);
        tokenBytes.Length.Should().Be(64);
    }

    #endregion

    #region RevokeRefreshTokensForUserAsync Tests

    [Fact]
    public async Task RevokeRefreshTokensForUserAsync_WithNullUserId_ReturnsEarly()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);

        // Act
        await service.RevokeRefreshTokensForUserAsync(null!);

        // Assert
        context.RefreshTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeRefreshTokensForUserAsync_WithEmptyUserId_ReturnsEarly()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);

        // Act
        await service.RevokeRefreshTokensForUserAsync("");

        // Assert
        context.RefreshTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeRefreshTokensForUserAsync_RemovesAllUserTokens()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();

        // Create multiple tokens for the user
        await service.GenerateRefreshTokenAsync(userId);
        await service.GenerateRefreshTokenAsync(userId);
        await service.GenerateRefreshTokenAsync(userId);

        var countBefore = await context.RefreshTokens.CountAsync();
        countBefore.Should().Be(3);

        // Act
        await service.RevokeRefreshTokensForUserAsync(userId);

        // Assert
        var countAfter = await context.RefreshTokens.CountAsync();
        countAfter.Should().Be(0);
    }

    [Fact]
    public async Task RevokeRefreshTokensForUserAsync_DoesNotAffectOtherUsers()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId1 = Guid.NewGuid().ToString();
        var userId2 = Guid.NewGuid().ToString();

        // Create tokens for both users
        await service.GenerateRefreshTokenAsync(userId1);
        await service.GenerateRefreshTokenAsync(userId1);
        await service.GenerateRefreshTokenAsync(userId2);
        await service.GenerateRefreshTokenAsync(userId2);

        // Act - Revoke only user1's tokens
        await service.RevokeRefreshTokensForUserAsync(userId1);

        // Assert - Only user2's tokens remain
        var remainingTokens = await context.RefreshTokens.ToListAsync();
        remainingTokens.Should().HaveCount(2);
        remainingTokens.Should().AllSatisfy(t => t.UserId.Should().Be(userId2));
    }

    [Fact]
    public async Task RevokeRefreshTokensForUserAsync_WithNoTokens_DoesNotThrow()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();

        // Act
        await service.RevokeRefreshTokensForUserAsync(userId);

        // Assert
        context.RefreshTokens.Should().BeEmpty();
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public async Task GenerateRefreshTokenAsync_ConcurrentCalls_AllSucceed()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var service = CreateService(context);
        var userId = Guid.NewGuid().ToString();

        // Act - Generate 10 tokens concurrently
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => service.GenerateRefreshTokenAsync(userId))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert - All tokens should be unique
        var uniqueTokens = results.Select(r => r.Token).Distinct().ToList();
        uniqueTokens.Should().HaveCount(10);
    }

    #endregion

    #region Helper Methods

    private AppIdentityDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<AppIdentityDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new AppIdentityDbContext(
            options,
            Mock.Of<IHostEnvironment>(),
            LoggerFactory,
            Mock.Of<ICurrentUserService>(),
            LoggerFactory.CreateLogger<AppIdentityDbContext>());
    }

    private RefreshTokenService CreateService(AppIdentityDbContext context, JwtOptions? options = null)
    {
        var opts = Options.Create(options ?? _jwtOptions);
        return new RefreshTokenService(
            context,
            opts,
            LoggerFactory.CreateLogger<RefreshTokenService>());
    }

    #endregion
}
