using FluentAssertions;
using Haworks.BuildingBlocks.Vault;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Vault;

public sealed class AgentFilePasswordProviderTests : IDisposable
{
    private readonly string _tempDir;

    public AgentFilePasswordProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vault-agent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ReadsCredentialsFromJsonFile()
    {
        var file = Path.Combine(_tempDir, "db-orders.json");
        await File.WriteAllTextAsync(file, """
            {
              "username": "orders_owner",
              "password": "rotated-secret-123"
            }
            """);

        var result = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);

        result.Should().NotBeNull();
        result!.Value.Username.Should().Be("orders_owner");
        result.Value.Password.Should().Be("rotated-secret-123");
        result.Value.Error.Should().BeNull();
    }

    [Fact]
    public async Task FileNotFound_ReturnsNull()
    {
        var file = Path.Combine(_tempDir, "nonexistent.json");

        var result = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MalformedJson_ReturnsErrorResult()
    {
        var file = Path.Combine(_tempDir, "db-bad.json");
        await File.WriteAllTextAsync(file, "not valid json {{{");

        var result = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);

        result.Should().NotBeNull();
        result!.Value.Error.Should().NotBeNull();
        result.Value.Password.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingPasswordProperty_ReturnsErrorResult()
    {
        var file = Path.Combine(_tempDir, "db-nopwd.json");
        await File.WriteAllTextAsync(file, """{ "username": "user1" }""");

        var result = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);

        result.Should().NotBeNull();
        result!.Value.Error.Should().BeOfType<KeyNotFoundException>();
    }

    [Fact]
    public async Task FileUpdated_NextCallReturnsNewPassword()
    {
        var file = Path.Combine(_tempDir, "db-payments.json");
        await File.WriteAllTextAsync(file, """
            { "username": "pay_user", "password": "password-v1" }
            """);

        var result1 = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);
        result1!.Value.Password.Should().Be("password-v1");

        await File.WriteAllTextAsync(file, """
            { "username": "pay_user", "password": "password-v2" }
            """);

        var result2 = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);
        result2!.Value.Password.Should().Be("password-v2");
    }

    [Fact]
    public async Task NullPassword_ReturnsErrorResult()
    {
        var file = Path.Combine(_tempDir, "db-null.json");
        await File.WriteAllTextAsync(file, """{ "username": "u", "password": null }""");

        var result = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);

        result.Should().NotBeNull();
        result!.Value.Error.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task UsernameOmitted_ReturnsNullUsername()
    {
        var file = Path.Combine(_tempDir, "db-nouser.json");
        await File.WriteAllTextAsync(file, """{ "password": "secret" }""");

        var result = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);

        result.Should().NotBeNull();
        result!.Value.Username.Should().BeNull();
        result.Value.Password.Should().Be("secret");
        result.Value.Error.Should().BeNull();
    }

    [Fact]
    public async Task EmptyFile_ReturnsErrorResult()
    {
        var file = Path.Combine(_tempDir, "db-empty.json");
        await File.WriteAllTextAsync(file, "");

        var result = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);

        result.Should().NotBeNull();
        result!.Value.Error.Should().NotBeNull();
        result.Value.Error!.Message.Should().Contain("empty");
    }

    [Fact]
    public async Task WhitespaceOnlyFile_ReturnsErrorResult()
    {
        var file = Path.Combine(_tempDir, "db-ws.json");
        await File.WriteAllTextAsync(file, "   \n  ");

        var result = await VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync(file);

        result.Should().NotBeNull();
        result!.Value.Error.Should().NotBeNull();
    }
}
