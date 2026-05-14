using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Haworks.BuildingBlocks.Testing.Containers;

/// <summary>
/// Lazy-singleton Elasticsearch container shared across integration tests.
/// <c>WithReuse(true)</c> means Testcontainers reuses the same container
/// across <c>dotnet test</c> invocations as long as the builder config hash
/// is unchanged — identical pattern to <see cref="SharedTestPostgres"/>.
/// </summary>
public static class SharedTestElasticsearch
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static IContainer? _container;
    private static int _mappedPort;

    private static async Task EnsureStartedAsync()
    {
        if (_container is { State: TestcontainersStates.Running })
            return;
        await _gate.WaitAsync();
        try
        {
            if (_container is null)
            {
                _container = new ContainerBuilder()
                    .WithImage("docker.elastic.co/elasticsearch/elasticsearch:8.17.0")
                    .WithEnvironment("discovery.type", "single-node")
                    .WithEnvironment("xpack.security.enabled", "false")
                    .WithEnvironment("ES_JAVA_OPTS", "-Xms256m -Xmx256m")
                    .WithPortBinding(9200, assignRandomHostPort: true)
                    .WithWaitStrategy(Wait.ForUnixContainer()
                        .UntilHttpRequestIsSucceeded(r => r.ForPath("/_cluster/health").ForPort(9200)))
                    .WithReuse(true)
                    .Build();
            }
            if (_container.State != TestcontainersStates.Running)
            {
                await _container.StartAsync();
                _mappedPort = _container.GetMappedPublicPort(9200);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Returns the Elasticsearch URL with a unique index name per caller,
    /// preventing test pollution across services.
    /// </summary>
    public static async Task<(string Url, string IndexName)> GetConnectionAsync(string serviceName)
    {
        await EnsureStartedAsync();
        var url = $"http://{_container!.Hostname}:{_mappedPort}";
        var indexName = $"{serviceName}_{Guid.NewGuid():N}".ToLowerInvariant();
        return (url, indexName);
    }
}
