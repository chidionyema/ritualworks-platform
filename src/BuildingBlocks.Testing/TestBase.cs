using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit.Abstractions;

namespace Haworks.BuildingBlocks.Testing;

public abstract class TestBase : IDisposable
{
    protected ITestOutputHelper Output { get; }
    protected MockRepository MockRepository { get; }
    protected IConfiguration TestConfig { get; }
    protected ILoggerFactory LoggerFactory { get; }

    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
        MockRepository = new MockRepository(MockBehavior.Strict);

        TestConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Address"] = "https://vault-test:8200",
                ["Vault:RoleIdPath"] = "test-data/role.id",
                ["Vault:SecretIdPath"] = "test-data/secret.id",
                ["Vault:ServerCertThumbprint"] = "TEST_THUMBPRINT",
                ["Resilience:MaxRetries"] = "3",
                ["Resilience:FailureThreshold"] = "0.6"
            })
            .Build();

        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }

    public virtual void Dispose()
    {
        MockRepository.VerifyAll();
        GC.SuppressFinalize(this);
    }
}
