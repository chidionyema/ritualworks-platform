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
        var anyFailed = false;
        foreach (var task in _tasks)
        {
            if (!await RunWithRetryAsync(task, stoppingToken))
            {
                anyFailed = true;
            }
        }

        if (anyFailed)
        {
            _logger.LogCritical("One or more startup tasks failed permanently — service will NOT become ready");
            throw new InvalidOperationException(
                "Startup tasks failed permanently. The host process should exit.");
        }

        _isReady = true;
        _logger.LogInformation("All startup tasks completed — service is ready");
    }

    /// <summary>
    /// Runs a startup task with one retry. Returns true on success, false on permanent failure.
    /// </summary>
    private async Task<bool> RunWithRetryAsync(
        Func<IServiceProvider, CancellationToken, Task> task,
        CancellationToken stoppingToken)
    {
        try
        {
            await task(_serviceProvider, stoppingToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup task failed — retrying in 5s");
            await Task.Delay(5000, stoppingToken);
            try
            {
                await task(_serviceProvider, stoppingToken);
                return true;
            }
            catch (Exception retryEx)
            {
                _logger.LogCritical(retryEx, "Startup task failed permanently");
                return false;
            }
        }
    }
}
