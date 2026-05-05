using FluentAssertions;
using Haworks.BuildingBlocks.Caching;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Testing;
using Haworks.Identity.Domain;
using Haworks.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;
using System.Collections.Concurrent;

namespace Haworks.Identity.Unit.Services;

public sealed class TokenRevocationServiceTests : TestBase
{
    public TokenRevocationServiceTests(ITestOutputHelper output) : base(output)
    {
    }

    #region RevokeTokenAsync Tests

    [Fact]
    public async Task RevokeTokenAsync_WithValidToken_PersistsToDatabase()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        var userId = Guid.NewGuid().ToString();
        var expiryDate = DateTime.UtcNow.AddMinutes(15);

        // Act
        await service.RevokeTokenAsync(tokenValue, userId, expiryDate);

        // Assert
        var revokedToken = await context.RevokedTokens.FirstOrDefaultAsync(rt => rt.Token == tokenValue);
        revokedToken.Should().NotBeNull();
        revokedToken!.Token.Should().Be(tokenValue);
        revokedToken.UserId.Should().Be(userId);
        revokedToken.ExpiresAt.Should().Be(expiryDate);
        revokedToken.Reason.Should().Be("Manual revocation");
    }

    [Fact]
    public async Task RevokeTokenAsync_WithValidToken_CachesRevocation()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        var userId = Guid.NewGuid().ToString();
        var expiryDate = DateTime.UtcNow.AddMinutes(15);

        // Act
        await service.RevokeTokenAsync(tokenValue, userId, expiryDate);

        // Assert
        cache.Contains("revoked_token:" + tokenValue).Should().BeTrue();
    }

    [Fact]
    public async Task RevokeTokenAsync_WithNullToken_ReturnsEarly()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var userId = Guid.NewGuid().ToString();
        var expiryDate = DateTime.UtcNow.AddMinutes(15);

        // Act
        await service.RevokeTokenAsync(null!, userId, expiryDate);

        // Assert
        context.RevokedTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeTokenAsync_WithEmptyToken_ReturnsEarly()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var userId = Guid.NewGuid().ToString();
        var expiryDate = DateTime.UtcNow.AddMinutes(15);

        // Act
        await service.RevokeTokenAsync("", userId, expiryDate);

        // Assert
        context.RevokedTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeTokenAsync_WithNullUserId_ReturnsEarly()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        var expiryDate = DateTime.UtcNow.AddMinutes(15);

        // Act
        await service.RevokeTokenAsync(tokenValue, null!, expiryDate);

        // Assert
        context.RevokedTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeTokenAsync_AlreadyRevoked_DoesNotDuplicate()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        var userId = Guid.NewGuid().ToString();
        var expiryDate = DateTime.UtcNow.AddMinutes(15);

        // Revoke once
        await service.RevokeTokenAsync(tokenValue, userId, expiryDate);

        // Act - Revoke again
        await service.RevokeTokenAsync(tokenValue, userId, expiryDate);

        // Assert - Should only have one entry
        var count = await context.RevokedTokens.CountAsync(rt => rt.Token == tokenValue);
        count.Should().Be(1);
    }

    [Fact]
    public async Task RevokeTokenAsync_WithExpiredDate_StillCachesWithFallbackTtl()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        var userId = Guid.NewGuid().ToString();
        var expiryDate = DateTime.UtcNow.AddMinutes(-5); // Already expired

        // Act
        await service.RevokeTokenAsync(tokenValue, userId, expiryDate);

        // Assert - Token persisted AND cached with a fallback TTL
        var revokedToken = await context.RevokedTokens.FirstOrDefaultAsync(rt => rt.Token == tokenValue);
        revokedToken.Should().NotBeNull();
        cache.Contains("revoked_token:" + tokenValue).Should().BeTrue();
    }

    [Fact]
    public async Task RevokeTokenAsync_SetsRevokedAtToUtcNow()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        var userId = Guid.NewGuid().ToString();
        var expiryDate = DateTime.UtcNow.AddMinutes(15);
        var beforeRevocation = DateTime.UtcNow;

        // Act
        await service.RevokeTokenAsync(tokenValue, userId, expiryDate);

        // Assert
        var afterRevocation = DateTime.UtcNow;
        var revokedToken = await context.RevokedTokens.FirstOrDefaultAsync(rt => rt.Token == tokenValue);
        revokedToken.Should().NotBeNull();
        revokedToken!.RevokedAt.Should().BeOnOrAfter(beforeRevocation).And.BeOnOrBefore(afterRevocation);
    }

    #endregion

    #region IsTokenRevokedAsync Tests

    [Fact]
    public async Task IsTokenRevokedAsync_WithNullToken_ReturnsFalse()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        // Act
        var result = await service.IsTokenRevokedAsync(null!);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsTokenRevokedAsync_WithEmptyToken_ReturnsFalse()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        // Act
        var result = await service.IsTokenRevokedAsync("");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsTokenRevokedAsync_WithRevokedTokenInCache_ReturnsTrue()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        // Pre-seed cache with a value (FakeHybridCache handles marker type via Activator)
        cache.SetRaw("revoked_token:" + tokenValue, new object());

        // Act
        var result = await service.IsTokenRevokedAsync(tokenValue);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsTokenRevokedAsync_WithRevokedTokenInDatabase_ReturnsTrue()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        context.RevokedTokens.Add(RevokedToken.Create(
            token: tokenValue,
            expiresAt: DateTime.UtcNow.AddMinutes(15),
            reason: "Test"));
        await context.SaveChangesAsync();

        // Act
        var result = await service.IsTokenRevokedAsync(tokenValue);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsTokenRevokedAsync_WithNonRevokedToken_ReturnsFalse()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        // Act
        var result = await service.IsTokenRevokedAsync("non-existent-token");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsTokenRevokedAsync_CachesResultFromDatabase()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        context.RevokedTokens.Add(RevokedToken.Create(
            token: tokenValue,
            expiresAt: DateTime.UtcNow.AddMinutes(15),
            reason: "Test"));
        await context.SaveChangesAsync();

        // Act
        await service.IsTokenRevokedAsync(tokenValue);

        // Assert - Should now be in cache
        cache.Contains("revoked_token:" + tokenValue).Should().BeTrue();
    }

    [Fact]
    public async Task IsTokenRevokedAsync_ChecksCacheBeforeDatabase()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";

        // Put in cache but not in database
        cache.SetRaw("revoked_token:" + tokenValue, new object());

        // Act
        var result = await service.IsTokenRevokedAsync(tokenValue);

        // Assert - Returns true from cache even though not in DB
        result.Should().BeTrue();
        context.RevokedTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task IsTokenRevokedAsync_WithExpiredTokenInDb_CachesForMinimum5Minutes()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        var cache = new FakeHybridCache();
        var service = CreateService(context, cache);

        var tokenValue = "test-token-jti";
        context.RevokedTokens.Add(RevokedToken.Create(
            token: tokenValue,
            expiresAt: DateTime.UtcNow.AddMinutes(-5), // Already expired
            reason: "Test"));
        await context.SaveChangesAsync();

        // Act
        var result = await service.IsTokenRevokedAsync(tokenValue);

        // Assert
        result.Should().BeTrue();
        cache.Contains("revoked_token:" + tokenValue).Should().BeTrue();
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

    private TokenRevocationService CreateService(AppIdentityDbContext context, IHybridCache cache)
    {
        return new TokenRevocationService(
            context,
            cache,
            LoggerFactory.CreateLogger<TokenRevocationService>());
    }

    #endregion

    private sealed class FakeHybridCache : IHybridCache
    {
        private readonly ConcurrentDictionary<string, object?> _store = new();

        public ValueTask<T?> GetOrCreateAsync<T>(
            string key,
            Func<CancellationToken, Task<T?>> factory,
            HybridCacheOptions? options = null,
            CancellationToken ct = default) where T : class
        {
            throw new NotImplementedException();
        }

        public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
        {
            if (_store.TryGetValue(key, out var existing))
            {
                if (existing is T cached)
                {
                    return new ValueTask<T?>(cached);
                }
                
                try
                {
                    return new ValueTask<T?>((T?)Activator.CreateInstance(typeof(T), nonPublic: true));
                }
                catch
                {
                    // Fallback
                }
            }
            return new ValueTask<T?>((T?)null);
        }

        public ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheOptions? options = null,
            CancellationToken ct = default) where T : class
        {
            _store[key] = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        {
            _store.TryRemove(key, out _);
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
        {
            foreach (var key in _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                _store.TryRemove(key, out _);
            }
            return ValueTask.CompletedTask;
        }

        public bool Contains(string key) => _store.ContainsKey(key);
        public void SetRaw(string key, object value) => _store[key] = value;
    }
}
