using FluentAssertions;
using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Vault;
using Haworks.BuildingBlocks.Vault.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Polly;
using VaultSharp;
using VaultSharp.V1;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.Token;
using VaultSharp.V1.Commons;
using VaultSharp.V1.SecretsEngines;
using VaultSharp.V1.SecretsEngines.Database;
using VaultSharp.V1.SecretsEngines.KeyValue;
using VaultSharp.V1.SecretsEngines.KeyValue.V2;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Vault;

public class VaultServiceTests
{
    private readonly Mock<IVaultClientFactory> _clientFactoryMock = new();
    private readonly Mock<IResiliencePolicyFactory> _policyFactoryMock = new();
    private readonly Mock<ITelemetryService> _telemetryMock = new();
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

    // ── Helper: build a mock IVaultClient with database + KV + auth stubs ──

    private Mock<IVaultClient> CreateMockVaultClient(
        string username = "vault-user",
        string password = "vault-pass",
        int leaseDurationSeconds = 3600)
    {
        var mockClient = new Mock<IVaultClient>();
        var mockV1 = new Mock<IVaultClientV1>();
        var mockSecrets = new Mock<ISecretsEngine>();
        var mockDatabase = new Mock<IDatabaseSecretsEngine>();
        var mockKeyValue = new Mock<IKeyValueSecretsEngine>();
        var mockKvV2 = new Mock<IKeyValueSecretsEngineV2>();
        var mockAuth = new Mock<IAuthMethod>();
        var mockTokenAuth = new Mock<ITokenAuthMethod>();

        // Wire the chain: client.V1.Secrets.Database / .KeyValue.V2 / .Auth.Token
        mockClient.Setup(c => c.V1).Returns(mockV1.Object);
        mockV1.Setup(v => v.Secrets).Returns(mockSecrets.Object);
        mockV1.Setup(v => v.Auth).Returns(mockAuth.Object);
        mockSecrets.Setup(s => s.Database).Returns(mockDatabase.Object);
        mockSecrets.Setup(s => s.KeyValue).Returns(mockKeyValue.Object);
        mockKeyValue.Setup(k => k.V2).Returns(mockKvV2.Object);
        mockAuth.Setup(a => a.Token).Returns(mockTokenAuth.Object);

        // Default: GetStaticCredentialsAsync returns valid creds
        var staticCreds = new StaticCredentials { Username = username, Password = password };
        var dbResponse = new Secret<StaticCredentials>
        {
            Data = staticCreds,
            LeaseDurationSeconds = leaseDurationSeconds
        };
        mockDatabase
            .Setup(d => d.GetStaticCredentialsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(dbResponse);

        // Default: RevokeSelfAsync succeeds
        mockTokenAuth
            .Setup(t => t.RevokeSelfAsync())
            .Returns(Task.CompletedTask);

        return mockClient;
    }

    private void SetupFactoryWithClient(Mock<IVaultClient> mockClient, TimeSpan? leaseDuration = null)
    {
        var handle = new VaultClientHandle(
            mockClient.Object,
            DateTime.UtcNow,
            leaseDuration ?? TimeSpan.FromHours(1));

        _clientFactoryMock
            .Setup(f => f.CreateClientAsync(It.IsAny<VaultOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
    }

    private void SetupKvSecret(Mock<IVaultClient> mockClient, string path, string key, string value)
    {
        var kvData = new Dictionary<string, object> { [key] = value };
        var secretData = new SecretData { Data = kvData };
        var secret = new Secret<SecretData> { Data = secretData };

        var mockKvV2 = Mock.Get(mockClient.Object.V1.Secrets.KeyValue.V2);
        mockKvV2
            .Setup(k => k.ReadSecretAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(secret);
    }

    // ── Constructor tests ──

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
    public void Constructor_WithMissingDbHost_DoesNotThrow()
    {
        // Database:Host is now optional (Neon/managed PG).
        // VaultService should not crash on startup when Host is empty.
        _dbOptions.Host = "";
        var service = CreateService();
        service.Should().NotBeNull();
    }

    // ── InitializeAsync tests ──

    [Fact]
    public async Task InitializeAsync_CallsClientFactory_OnFirstCall()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        await svc.InitializeAsync();

        _clientFactoryMock.Verify(
            f => f.CreateClientAsync(It.IsAny<VaultOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_SecondCall_DoesNotReauthenticate()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        await svc.InitializeAsync();
        await svc.InitializeAsync();

        _clientFactoryMock.Verify(
            f => f.CreateClientAsync(It.IsAny<VaultOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_ConcurrentCalls_OnlyOneLogin()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => svc.InitializeAsync())
            .ToArray();

        await Task.WhenAll(tasks);

        _clientFactoryMock.Verify(
            f => f.CreateClientAsync(It.IsAny<VaultOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── GetDatabaseCredentialsAsync tests ──

    [Fact]
    public async Task GetDatabaseCredentialsAsync_FirstCall_FetchesFromVault()
    {
        var mockClient = CreateMockVaultClient(username: "db-user", password: "db-pass");
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        var (username, password) = await svc.GetDatabaseCredentialsAsync("haworks-catalog");

        username.Should().Be("db-user");
        password.Should().Be("db-pass");
    }

    [Fact]
    public async Task GetDatabaseCredentialsAsync_CachedWithinTtl_ReturnsCached()
    {
        var mockClient = CreateMockVaultClient(username: "db-user", password: "db-pass", leaseDurationSeconds: 3600);
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        await svc.GetDatabaseCredentialsAsync("haworks-catalog");
        await svc.GetDatabaseCredentialsAsync("haworks-catalog");

        // GetStaticCredentialsAsync should only be called once — second call uses cache
        var mockDatabase = Mock.Get(mockClient.Object.V1.Secrets.Database);
        mockDatabase.Verify(
            d => d.GetStaticCredentialsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task GetDatabaseCredentialsAsync_ExpiredCache_RefreshesFromVault()
    {
        // First call returns creds with 0s TTL (already expired)
        var mockClient = CreateMockVaultClient(username: "old-user", password: "old-pass", leaseDurationSeconds: 0);
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        await svc.GetDatabaseCredentialsAsync("haworks-catalog");

        // Second call should hit Vault again because TTL=0 means immediately expired
        await svc.GetDatabaseCredentialsAsync("haworks-catalog");

        var mockDatabase = Mock.Get(mockClient.Object.V1.Secrets.Database);
        mockDatabase.Verify(
            d => d.GetStaticCredentialsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task GetDatabaseCredentialsAsync_CalledBeforeInit_AutoInitializes()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        // Do NOT call InitializeAsync — GetDatabaseCredentialsAsync should self-init
        await svc.GetDatabaseCredentialsAsync("haworks-catalog");

        _clientFactoryMock.Verify(
            f => f.CreateClientAsync(It.IsAny<VaultOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── GetKvSecretAsync tests ──

    [Fact]
    public async Task GetKvSecretAsync_ReturnsValue_WhenKeyExists()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        SetupKvSecret(mockClient, "stripe", "secret_key", "sk_test_abc123");
        using var svc = CreateService();

        var result = await svc.GetKvSecretAsync("stripe", "secret_key");

        result.Should().Be("sk_test_abc123");
    }

    [Fact]
    public async Task GetKvSecretAsync_ReturnsNull_WhenPathNotFound()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);

        // Return null Data to simulate path not found
        var secret = new Secret<SecretData> { Data = new SecretData { Data = null! } };
        var mockKvV2 = Mock.Get(mockClient.Object.V1.Secrets.KeyValue.V2);
        mockKvV2
            .Setup(k => k.ReadSecretAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(secret);

        using var svc = CreateService();

        var result = await svc.GetKvSecretAsync("nonexistent", "key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetKvSecretAsync_ReturnsNull_WhenVaultUnavailable()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);

        var mockKvV2 = Mock.Get(mockClient.Object.V1.Secrets.KeyValue.V2);
        mockKvV2
            .Setup(k => k.ReadSecretAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("Vault unreachable"));

        using var svc = CreateService();

        var result = await svc.GetKvSecretAsync("stripe", "secret_key");

        result.Should().BeNull();
    }

    // ── Token lifecycle tests ──

    [Fact]
    public async Task GetClientAsync_RefreshesToken_WhenNearExpiry()
    {
        var mockClient = CreateMockVaultClient();
        // First handle with a very short lease (already near expiry)
        var shortHandle = new VaultClientHandle(
            mockClient.Object,
            DateTime.UtcNow.AddMinutes(-60), // created 60 min ago
            TimeSpan.FromMinutes(1));         // 1 min lease = already expired

        // Second handle with a fresh lease
        var freshHandle = new VaultClientHandle(
            mockClient.Object,
            DateTime.UtcNow,
            TimeSpan.FromHours(1));

        _clientFactoryMock
            .SetupSequence(f => f.CreateClientAsync(It.IsAny<VaultOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(shortHandle)
            .ReturnsAsync(freshHandle);

        using var svc = CreateService();
        await svc.InitializeAsync();

        // This call should trigger re-auth because the token is near/past expiry
        await svc.GetDatabaseCredentialsAsync("haworks-catalog");

        // Factory called twice: once for init, once for refresh
        _clientFactoryMock.Verify(
            f => f.CreateClientAsync(It.IsAny<VaultOptions>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RevokeTokenAsync_CallsRevokeSelf()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();
        await svc.InitializeAsync();

        await svc.RevokeTokenAsync();

        var mockTokenAuth = Mock.Get(mockClient.Object.V1.Auth.Token);
        mockTokenAuth.Verify(t => t.RevokeSelfAsync(), Times.Once);
    }

    [Fact]
    public async Task RevokeTokenAsync_SwallowsException()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);

        var mockTokenAuth = Mock.Get(mockClient.Object.V1.Auth.Token);
        mockTokenAuth
            .Setup(t => t.RevokeSelfAsync())
            .ThrowsAsync(new HttpRequestException("Vault down"));

        using var svc = CreateService();
        await svc.InitializeAsync();

        // Should not throw
        var act = () => svc.RevokeTokenAsync();
        await act.Should().NotThrowAsync();
    }

    // ── Dispose tests ──

    [Fact]
    public async Task Dispose_ClearsCache()
    {
        var mockClient = CreateMockVaultClient(leaseDurationSeconds: 3600);
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        await svc.GetDatabaseCredentialsAsync("haworks-catalog");

        // Verify cache has data before dispose
        svc.LeaseExpiryFor("haworks-catalog").Should().BeAfter(DateTime.UtcNow);

        svc.Dispose();

        // After dispose, cache is cleared — LeaseExpiryFor returns DateTime.MinValue
        svc.LeaseExpiryFor("haworks-catalog").Should().Be(DateTime.MinValue);
    }

    [Fact]
    public Task GetDatabaseCredentialsAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        var svc = CreateService();
        svc.Dispose();

        var act = () => svc.GetDatabaseCredentialsAsync("haworks-catalog");

        return act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // ── Renewal loop tests (QA-01) ──

    [Fact]
    public async Task StartCredentialRenewalAsync_RefreshesAllCachedRoles()
    {
        var mockClient = CreateMockVaultClient(leaseDurationSeconds: 1); // short TTL forces refresh
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        // Prime cache for two roles
        await svc.GetDatabaseCredentialsAsync("haworks-catalog");
        await svc.GetDatabaseCredentialsAsync("haworks-orders");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try { await svc.StartCredentialRenewalAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        var mockDb = Mock.Get(mockClient.Object.V1.Secrets.Database);
        // At least 2 init calls + renewal calls for both roles
        mockDb.Verify(
            d => d.GetStaticCredentialsAsync("haworks-catalog", It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeast(2));
        mockDb.Verify(
            d => d.GetStaticCredentialsAsync("haworks-orders", It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task StartCredentialRenewalAsync_HandlesErrorWithoutDying()
    {
        var mockClient = CreateMockVaultClient(leaseDurationSeconds: 1);
        SetupFactoryWithClient(mockClient);

        var mockDb = Mock.Get(mockClient.Object.V1.Secrets.Database);
        var callCount = 0;
        mockDb
            .Setup(d => d.GetStaticCredentialsAsync("haworks-catalog", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 2) throw new HttpRequestException("Vault down");
                return new VaultSharp.V1.Commons.Secret<StaticCredentials>
                {
                    Data = new StaticCredentials { Username = "u", Password = "p" },
                    LeaseDurationSeconds = 1
                };
            });

        using var svc = CreateService();
        await svc.GetDatabaseCredentialsAsync("haworks-catalog");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try { await svc.StartCredentialRenewalAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        // Loop survived the error — called more than twice
        callCount.Should().BeGreaterThan(2);
    }

    // ── Connection string tests (QA-05) ──

    [Fact]
    public async Task GetDatabaseConnectionStringAsync_BuildsCorrectConnectionString()
    {
        _dbOptions.Host = "db.example.com";
        _dbOptions.Database = "mydb";
        _dbOptions.Port = 5432;
        _dbOptions.SslMode = "Require";

        var mockClient = CreateMockVaultClient(username: "user1", password: "pass1");
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        var connStr = await svc.GetDatabaseConnectionStringAsync("haworks-catalog");

        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(connStr);
        parsed.Host.Should().Be("db.example.com");
        parsed.Database.Should().Be("mydb");
        parsed.Port.Should().Be(5432);
        parsed.Username.Should().Be("user1");
        parsed.Password.Should().Be("pass1");
        parsed.SslMode.Should().Be(Npgsql.SslMode.Require);
    }

    [Fact]
    public async Task GetDatabaseConnectionStringAsync_ThrowsWhenHostEmpty()
    {
        _dbOptions.Host = "";
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        var act = () => svc.GetDatabaseConnectionStringAsync("haworks-catalog");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Database:Host*");
    }

    // ── Role validation tests (QA-09) ──

    [Fact]
    public async Task GetDatabaseCredentialsAsync_EmptyRoleName_ThrowsArgumentException()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        var act = () => svc.GetDatabaseCredentialsAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetDatabaseCredentialsAsync_NullRoleName_ThrowsArgumentException()
    {
        var mockClient = CreateMockVaultClient();
        SetupFactoryWithClient(mockClient);
        using var svc = CreateService();

        var act = () => svc.GetDatabaseCredentialsAsync(null!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private VaultService CreateService()
    {
        return new VaultService(
            Options.Create(_vaultOptions),
            Options.Create(_dbOptions),
            _clientFactoryMock.Object,
            _policyFactoryMock.Object,
            _loggerMock.Object,
            _telemetryMock.Object);
    }
}
