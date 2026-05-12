using Haworks.Cdc.Application.Interfaces;
using Haworks.Cdc.Application.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Haworks.Cdc.Application;

public sealed class CdcRelayManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CdcRelayManager> _logger;
    private readonly Dictionary<string, (ICdcRelay Relay, Task Task, CancellationTokenSource Cts)> _activeRelays = new();

    public CdcRelayManager(IServiceProvider serviceProvider, ILogger<CdcRelayManager> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public void StartRelay(string serviceName, ReplicationOptions options)
    {
        if (_activeRelays.ContainsKey(serviceName)) return;

        var cts = new CancellationTokenSource();
        
        // We use the service provider to resolve ICdcRelay. 
        // The implementation is in Infrastructure and registered via reflection in DependencyInjection.T1.cs.
        // We need a way to pass options to the implementation.
        // Since ActivatorUtilities or similar might be complex with scope, 
        // we'll use a Factory pattern or just register options in a scope.
        
        var scope = _serviceProvider.CreateScope();
        
        // TEMPORARY: For T2 to work with the T1 implementation without tight coupling,
        // we'll assume the ICdcRelay implementation can be created.
        // In a real scenario, ICdcRelay implementation would take an IOptions or similar.
        
        // For now, let's use ActivatorUtilities to create the implementation if we can find it,
        // or assume it's registered in DI.
        
        var relay = scope.ServiceProvider.GetService<ICdcRelay>();
        if (relay == null)
        {
            // Fallback: try to find the type again (similar to DI)
            var relayImplType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .FirstOrDefault(t => t.Name == "PostgresLogicalReplicationSubscriber" && typeof(ICdcRelay).IsAssignableFrom(t));
                
            if (relayImplType != null)
            {
                relay = (ICdcRelay)ActivatorUtilities.CreateInstance(scope.ServiceProvider, relayImplType, options);
            }
        }

        if (relay != null)
        {
            var task = Task.Run(() => relay.RunAsync(cts.Token), cts.Token);
            _activeRelays[serviceName] = (relay, task, cts);
            _logger.LogInformation("Started CDC relay for {ServiceName}", serviceName);
        }
        else
        {
            _logger.LogError("Could not create ICdcRelay implementation for {ServiceName}", serviceName);
        }
    }

    public async Task StopRelayAsync(string serviceName)
    {
        if (_activeRelays.Remove(serviceName, out var entry))
        {
            entry.Cts.Cancel();
            try 
            {
                await entry.Task;
            }
            catch (OperationCanceledException) { }
            entry.Cts.Dispose();
            _logger.LogInformation("Stopped CDC relay for {ServiceName}", serviceName);
        }
    }

    public IReadOnlyCollection<string> GetActiveSources() => _activeRelays.Keys.ToList();
}
