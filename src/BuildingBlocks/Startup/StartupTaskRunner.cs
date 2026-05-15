using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Startup;

/// <summary>
/// Runs startup tasks (migrations, Vault init, etc.) in the background.
/// Services start serving immediately; /health/ready reflects completion.
/// </summary>
public sealed class StartupTaskRunner : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StartupTaskRunner> _logger;
    private readonly List<Func<IServiceProvider, CancellationToken, Task>> _tasks = new();
    private volatile bool _isReady;

    public bool IsReady => _isReady;

    public StartupTaskRunner(IServiceProvider serviceProvider, ILogger<StartupTaskRunner> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public void AddTask(Func<IServiceProvider, CancellationToken, Task> task) => _tasks.Add(task);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var task in _tasks)
        {
            try
            {
                await task(_serviceProvider, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup task failed — retrying in 5s");
                await Task.Delay(5000, stoppingToken);
                // Retry once
                try { await task(_serviceProvider, stoppingToken); }
                catch (Exception retryEx) { _logger.LogCritical(retryEx, "Startup task failed permanently"); }
            }
        }
        _isReady = true;
        _logger.LogInformation("All startup tasks completed — service is ready");
    }
}
