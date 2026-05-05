using FluentAssertions;
using Haworks.BuildingBlocks.Caching;
using Haworks.Identity.Application.Interfaces;
using Haworks.Identity.Domain;
using Haworks.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using System.Collections.Concurrent;

namespace Haworks.Identity.Unit.Services;

public class UserEmailServiceTests
{
    private readonly Mock<UserManager<User>> _userManagerMock;
    private readonly FakeHybridCache _cache;
    private readonly UserEmailService _service;

    public UserEmailServiceTests()
    {
        var storeMock = new Mock<IUserStore<User>>();
        _userManagerMock = new Mock<UserManager<User>>(storeMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        _cache = new FakeHybridCache();
        _service = new UserEmailService(_userManagerMock.Object, _cache, Mock.Of<ILogger<UserEmailService>>());
    }

    [Fact]
    public async Task GetUserEmailAsync_WithValidUserId_QueriesUserManagerAndCaches()
    {
        var userId = "user-123";
        var user = new User { Id = userId, Email = "user@example.com" };
        _userManagerMock.Setup(x => x.FindByIdAsync(userId)).ReturnsAsync(user);

        var result1 = await _service.GetUserEmailAsync(userId);
        var result2 = await _service.GetUserEmailAsync(userId);

        result1.Should().Be("user@example.com");
        result2.Should().Be("user@example.com");
        _userManagerMock.Verify(x => x.FindByIdAsync(userId), Times.Once);
    }

    [Fact]
    public async Task GetUserEmailAsync_WithNullUserId_ReturnsNull()
    {
        var result = await _service.GetUserEmailAsync(null!);
        result.Should().BeNull();
    }

    [Fact]
    public async Task InvalidateCache_RemovesCachedValue()
    {
        var userId = "user-123";
        _cache.SetRaw("user_email:" + userId, "old@example.com");
        
        _service.InvalidateCache(userId);
        
        // Wait for background Task.Run to complete
        for(int i=0; i<10 && _cache.Contains("user_email:" + userId); i++) await Task.Delay(50);
        
        _cache.Contains("user_email:" + userId).Should().BeFalse();
    }

    private sealed class FakeHybridCache : IHybridCache
    {
        private readonly ConcurrentDictionary<string, object?> _store = new();
        public ValueTask<T?> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, HybridCacheOptions? options = null, CancellationToken ct = default) where T : class
        {
            if (_store.TryGetValue(key, out var existing)) return new ValueTask<T?>((T?)existing);
            var value = factory(ct).GetAwaiter().GetResult();
            _store[key] = value;
            return new ValueTask<T?>(value);
        }
        public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class => throw new NotImplementedException();
        public ValueTask SetAsync<T>(string key, T value, HybridCacheOptions? options = null, CancellationToken ct = default) where T : class => throw new NotImplementedException();
        public ValueTask RemoveAsync(string key, CancellationToken ct = default) { _store.TryRemove(key, out _); return ValueTask.CompletedTask; }
        public ValueTask RemoveByPrefixAsync(string prefix, CancellationToken ct = default) => throw new NotImplementedException();
        public bool Contains(string key) => _store.ContainsKey(key);
        public void SetRaw(string key, object value) => _store[key] = value;
    }
}
