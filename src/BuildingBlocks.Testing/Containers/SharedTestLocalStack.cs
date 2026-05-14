using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Haworks.BuildingBlocks.Testing.Containers;

/// <summary>
/// Lazy-singleton LocalStack container for S3-dependent integration tests.
/// Same reuse pattern as <see cref="SharedTestPostgres"/>.
/// </summary>
public static class SharedTestS3
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static IContainer? _container;
    private static int _mappedPort;

    public static async Task<string> GetEndpointAsync()
    {
        if (_container is { State: TestcontainersStates.Running })
            return $"http://{_container.Hostname}:{_mappedPort}";
        await _gate.WaitAsync();
        try
        {
            if (_container is null)
            {
                _container = new ContainerBuilder()
                    .WithImage("localstack/localstack:3")
                    .WithEnvironment("SERVICES", "s3")
                    .WithEnvironment("DEFAULT_REGION", "us-east-1")
                    .WithPortBinding(4566, assignRandomHostPort: true)
                    .WithWaitStrategy(Wait.ForUnixContainer()
                        .UntilHttpRequestIsSucceeded(r => r.ForPath("/_localstack/health").ForPort(4566)))
                    .WithReuse(true)
                    .Build();
            }
            if (_container.State != TestcontainersStates.Running)
            {
                await _container.StartAsync();
                _mappedPort = _container.GetMappedPublicPort(4566);
            }
            return $"http://{_container.Hostname}:{_mappedPort}";
        }
        finally { _gate.Release(); }
    }
}
