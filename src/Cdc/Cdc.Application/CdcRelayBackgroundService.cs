using Haworks.Cdc.Application.Interfaces;
using Haworks.Cdc.Application.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Cdc.Application;

public sealed class CdcRelayBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CdcRelayManager _relayManager;
    private readonly ILogger<CdcRelayBackgroundService> _logger;

    public CdcRelayBackgroundService(
        IServiceProvider serviceProvider,
        CdcRelayManager relayManager,
        ILogger<CdcRelayBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _relayManager = relayManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CDC Relay Background Service starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncRelaysAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync CDC relays");
            }

            await Task.Delay(15000, stoppingToken);
        }
    }

    private async Task SyncRelaysAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICdcStore>();

        var sources = await store.GetEnabledSourcesAsync(ct);
        var activeSources = _relayManager.GetActiveSources();

        // 1. Start new relays
        foreach (var source in sources)
        {
            if (!activeSources.Contains(source.ServiceName))
            {
                _relayManager.StartRelay(source.ServiceName, new ReplicationOptions
                {
                    ConnectionString = source.ConnectionString,
                    SlotName = source.SlotName,
                    PublicationName = source.PublicationName,
                    SourceService = source.ServiceName
                });
            }
        }

        // 2. Stop removed or disabled relays
        var desiredSourceNames = sources.Select(s => s.ServiceName).ToList();
        foreach (var activeSource in activeSources)
        {
            if (!desiredSourceNames.Contains(activeSource))
            {
                await _relayManager.StopRelayAsync(activeSource);
            }
        }
    }
}
