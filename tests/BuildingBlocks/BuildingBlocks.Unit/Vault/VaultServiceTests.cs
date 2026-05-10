using FluentAssertions;
using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Vault;
using Haworks.BuildingBlocks.Vault.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Polly;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Vault;

public class VaultServiceTests
{
    private readonly Mock<IVaultClientFactory> _clientFactoryMock = new();
    private readonly Mock<IResiliencePolicyFactory> _policyFactoryMock = new();
    private readonly Mock<ITelemetryService> _telemetryMock = new();
    private readonly Mock<ICredentialStore> _credentialStoreMock = new();
    private readonly Mock<ILogger<VaultService>> _loggerMock = new();

    private readonly VaultOptions _vaultOptions = new()
    {
        Address = "https://vault:8200",
        RoleIdPath = "role.id",
        SecretIdPath = "secret.id"
    };

    private readonly DatabaseOptions _dbOptions = new()
    {
        Host = "localhost",
        Database = "db"
    };

    public VaultServiceTests()
    {
        _policyFactoryMock
            .Setup(p => p.CreateCircuitBreaker(It.IsAny<ResilienceOptions>(), It.IsAny<Action<Exception, TimeSpan>>(), It.IsAny<Action>()))
            .Returns(Policy.Handle<Exception>().CircuitBreakerAsync(5, TimeSpan.FromSeconds(30)));

        _policyFactoryMock
            .Setup(p => p.CreateRetryPolicy(It.IsAny<ResilienceOptions>(), It.IsAny<Action<Exception, TimeSpan, int>>()))
            .Returns(Policy.Handle<Exception>().WaitAndRetryAsync(3, _ => TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void Constructor_WithValidOptions_DoesNotThrow()
    {
        var service = CreateService();
        service.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_WithInvalidAddress_ThrowsArgumentNullException(string? address)
    {
        _vaultOptions.Address = address!;
        Assert.Throws<ArgumentNullException>(() => CreateService());
    }

    [Fact]
    public void Constructor_WithNoCredsAtAll_ThrowsInvalidOperation()
    {
        _vaultOptions.RoleIdPath = "";
        _vaultOptions.SecretIdPath = "";
        _vaultOptions.RoleId = "";
        _vaultOptions.SecretId = "";
        Assert.Throws<InvalidOperationException>(() => CreateService());
    }

    [Fact]
    public void Constructor_WithDirectCreds_DoesNotThrow()
    {
        _vaultOptions.RoleIdPath = "";
        _vaultOptions.SecretIdPath = "";
        _vaultOptions.RoleId = "00000000-0000-0000-0000-000000000001";
        _vaultOptions.SecretId = "00000000-0000-0000-0000-000000000002";

        var service = CreateService();
        service.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithMissingDbHost_ThrowsArgumentNullException()
    {
        _dbOptions.Host = "";
        Assert.Throws<ArgumentNullException>(() => CreateService());
    }

    private VaultService CreateService()
    {
        return new VaultService(
            Options.Create(_vaultOptions),
            Options.Create(_dbOptions),
            _clientFactoryMock.Object,
            _policyFactoryMock.Object,
            () => _credentialStoreMock.Object,
            _loggerMock.Object,
            _telemetryMock.Object);
    }
}
