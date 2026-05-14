using Testcontainers.RabbitMq;

namespace Haworks.BuildingBlocks.Testing.Containers;

/// <summary>
/// Lazy-singleton RabbitMQ container shared across integration tests.
/// Same reuse pattern as <see cref="SharedTestPostgres"/>.
/// </summary>
public static class SharedTestRabbitMq
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static RabbitMqContainer? _container;

    public static async Task<string> GetConnectionStringAsync()
    {
        if (_container is { State: DotNet.Testcontainers.Containers.TestcontainersStates.Running })
            return _container.GetConnectionString();
        await _gate.WaitAsync();
        try
        {
            if (_container is null)
            {
                _container = new RabbitMqBuilder()
                    .WithImage("rabbitmq:3-management")
                    .WithReuse(true)
                    .Build();
            }
            if (_container.State != DotNet.Testcontainers.Containers.TestcontainersStates.Running)
                await _container.StartAsync();
            return _container.GetConnectionString();
        }
        finally { _gate.Release(); }
    }
}
