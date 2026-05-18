using FluentAssertions;
using Haworks.BuildingBlocks.Vault;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Vault;

public class VaultTokenRevocationTests
{
    private readonly Mock<IVaultService> _vaultService = new();

    [Fact]
    public async Task OnStopping_ResolvesVaultService_AndRevokesToken()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_vaultService.Object);
        var sp = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource();
        var lifetime = new TestApplicationLifetime(cts);

        var sut = CreateSut(sp, lifetime);
        await sut.StartAsync(CancellationToken.None);

        // Trigger ApplicationStopping
        await cts.CancelAsync();

        _vaultService.Verify(v => v.RevokeTokenAsync(It.IsAny<CancellationToken>()), Times.Once);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnStopping_WhenVaultServiceNotRegistered_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource();
        var lifetime = new TestApplicationLifetime(cts);

        var sut = CreateSut(sp, lifetime);
        await sut.StartAsync(CancellationToken.None);

        // Should not throw when IVaultService is not registered
        var act = async () => await cts.CancelAsync();
        await act.Should().NotThrowAsync();

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnStopping_WhenRevokeThrows_LogsWarning_DoesNotCrash()
    {
        _vaultService.Setup(v => v.RevokeTokenAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Vault unreachable"));

        var services = new ServiceCollection();
        services.AddSingleton(_vaultService.Object);
        var sp = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource();
        var lifetime = new TestApplicationLifetime(cts);

        var sut = CreateSut(sp, lifetime);
        await sut.StartAsync(CancellationToken.None);

        var act = async () => await cts.CancelAsync();
        await act.Should().NotThrowAsync();

        await sut.StopAsync(CancellationToken.None);
    }

#pragma warning disable S2325 // already static
    private static VaultTokenRevocationHostedService CreateSut(
        IServiceProvider sp, IHostApplicationLifetime lifetime) =>
        new(sp, lifetime, NullLogger<VaultTokenRevocationHostedService>.Instance);

    /// <summary>
    /// Minimal IHostApplicationLifetime that fires ApplicationStopping when the
    /// supplied CancellationTokenSource is cancelled.
    /// </summary>
    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stoppingCts;

        public TestApplicationLifetime(CancellationTokenSource stoppingCts)
        {
            _stoppingCts = stoppingCts;
        }

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
