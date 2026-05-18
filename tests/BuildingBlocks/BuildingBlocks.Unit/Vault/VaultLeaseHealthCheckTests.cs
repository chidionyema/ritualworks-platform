using FluentAssertions;
using Haworks.BuildingBlocks.Vault;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Vault;

public class VaultLeaseHealthCheckTests
{
    private readonly Mock<IVaultCredentialProvider> _provider = new();
    private readonly Mock<ILogger<VaultLeaseHealthCheck>> _logger = new();
    private const string RoleName = "test-role";

    private VaultLeaseHealthCheck CreateSut() => new(_provider.Object, RoleName, _logger.Object);

    [Fact]
    public async Task WhenNoCredentialsFetched_FetchesOnFirstCheck_ReturnsHealthy()
    {
        _provider.Setup(p => p.GetLeaseStatus()).Returns(new VaultLeaseStatus
        {
            HasCredentials = false,
            CachedUntil = DateTimeOffset.MinValue,
            FetchedAt = DateTimeOffset.MinValue,
            TtlPercentElapsed = 0.0
        });

        _provider.Setup(p => p.GetDatabaseCredentialsAsync(RoleName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(("user", "pass"));

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("fetched successfully on first check");
        _provider.Verify(p => p.GetDatabaseCredentialsAsync(RoleName, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenTtlBelow90Percent_ReturnsHealthy()
    {
        _provider.Setup(p => p.GetLeaseStatus()).Returns(new VaultLeaseStatus
        {
            HasCredentials = true,
            CachedUntil = DateTimeOffset.UtcNow.AddMinutes(30),
            FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            TtlPercentElapsed = 0.5
        });

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("are valid");
        result.Data.Should().ContainKey("ttlPercent").WhoseValue.Should().Be(0.5);
    }

    [Fact]
    public async Task WhenTtlAbove90Percent_ReturnsDegraded()
    {
        _provider.Setup(p => p.GetLeaseStatus()).Returns(new VaultLeaseStatus
        {
            HasCredentials = true,
            CachedUntil = DateTimeOffset.UtcNow.AddSeconds(10),
            FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-50),
            TtlPercentElapsed = 0.95
        });

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("TTL");
        result.Data.Should().ContainKey("ttlPercent").WhoseValue.Should().Be(0.95);
    }

    [Fact]
    public async Task WhenCredentialsExpired_ReturnsUnhealthy()
    {
        var expired = DateTimeOffset.UtcNow.AddMinutes(-5);
        _provider.Setup(p => p.GetLeaseStatus()).Returns(new VaultLeaseStatus
        {
            HasCredentials = true,
            CachedUntil = expired,
            FetchedAt = expired.AddHours(-1),
            TtlPercentElapsed = 1.1
        });

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("expired");
    }

    [Fact]
    public async Task WhenFetchThrows_ReturnsUnhealthy()
    {
        _provider.Setup(p => p.GetLeaseStatus()).Returns(new VaultLeaseStatus
        {
            HasCredentials = false,
            CachedUntil = DateTimeOffset.MinValue,
            FetchedAt = DateTimeOffset.MinValue,
            TtlPercentElapsed = 0.0
        });

        _provider.Setup(p => p.GetDatabaseCredentialsAsync(RoleName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Vault unreachable"));

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Vault unreachable");
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }
}
