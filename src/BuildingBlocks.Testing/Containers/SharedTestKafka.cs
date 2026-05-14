using Testcontainers.Kafka;

namespace Haworks.BuildingBlocks.Testing.Containers;

/// <summary>
/// Lazy-singleton Kafka container shared across integration tests.
/// Same reuse pattern as <see cref="SharedTestPostgres"/>.
/// </summary>
public static class SharedTestKafka
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static KafkaContainer? _container;

    public static async Task<string> GetBootstrapAddressAsync()
    {
        if (_container is { State: DotNet.Testcontainers.Containers.TestcontainersStates.Running })
            return _container.GetBootstrapAddress();
        await _gate.WaitAsync();
        try
        {
            if (_container is null)
            {
                _container = new KafkaBuilder()
                    .WithImage("confluentinc/cp-kafka:7.6.1")
                    .WithReuse(true)
                    .Build();
            }
            if (_container.State != DotNet.Testcontainers.Containers.TestcontainersStates.Running)
                await _container.StartAsync();
            return _container.GetBootstrapAddress();
        }
        finally { _gate.Release(); }
    }
}
