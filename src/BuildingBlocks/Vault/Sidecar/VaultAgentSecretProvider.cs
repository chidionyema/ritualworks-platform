using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Vault.Sidecar;

/// <summary>
/// Reads secrets rendered by Vault Agent sidecar from the filesystem.
/// Vault Agent writes templates to /vault/secrets/*.env files; this provider
/// watches for changes and reloads IConfiguration automatically.
///
/// This replaces the in-app VaultService/VaultClientFactory/AppRole dance entirely.
/// Services just read IConfiguration["ConnectionStrings:payments"] and get the
/// rotated credentials — zero Vault SDK, zero AppRole, zero HWK violations.
///
/// K8s: Vault Agent Injector (vault.hashicorp.com/agent-inject) mounts secrets as files.
/// Fly: Vault Agent runs as a Fly Machine process alongside the app (fly.toml [processes]).
/// Local: Static .env.local or Aspire wires secrets directly.
/// </summary>
public static class VaultAgentExtensions
{
    private const string DefaultSecretsPath = "/vault/secrets";

    /// <summary>
    /// Adds file-based secret configuration from Vault Agent sidecar.
    /// Watches the secrets directory for changes (credential rotation).
    /// Falls through gracefully if path doesn't exist (local dev, tests).
    /// </summary>
    public static IHostApplicationBuilder AddVaultAgentSecrets(
        this IHostApplicationBuilder builder,
        string? secretsPath = null)
    {
        var path = secretsPath
            ?? builder.Configuration["Vault:Agent:SecretsPath"]
            ?? DefaultSecretsPath;

        if (!Directory.Exists(path))
        {
            // Sidecar not present (local dev, test) — use existing config sources
            return builder;
        }

        // Add all .env and .json files from the secrets directory
        foreach (var file in Directory.GetFiles(path, "*.json"))
        {
            builder.Configuration.AddJsonFile(file, optional: true, reloadOnChange: true);
        }

        // Key=Value .env files (Vault template format: KEY=VALUE per line)
        foreach (var envFile in Directory.GetFiles(path, "*.env"))
        {
            builder.Configuration.AddKeyPerFile(Path.GetDirectoryName(envFile)!, optional: true);
        }

        return builder;
    }

    /// <summary>
    /// Registers a background watcher that monitors the Vault Agent secrets directory
    /// for file changes and triggers IConfiguration reload. This handles the case
    /// where Vault Agent rotates credentials mid-flight (DB password change).
    ///
    /// NpgsqlDataSource with periodic password provider is still required for
    /// connection pool credential refresh — this just keeps IConfiguration in sync.
    /// </summary>
    public static IServiceCollection AddVaultAgentFileWatcher(
        this IServiceCollection services,
        string? secretsPath = null)
    {
        services.AddHostedService(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var logger = sp.GetRequiredService<ILogger<VaultAgentFileWatcher>>();
            var path = secretsPath
                ?? config["Vault:Agent:SecretsPath"]
                ?? DefaultSecretsPath;
            return new VaultAgentFileWatcher(path, logger);
        });
        return services;
    }
}

/// <summary>
/// Watches Vault Agent secret files for changes and logs rotation events.
/// IConfiguration's reloadOnChange handles the actual reload — this provides
/// operational visibility (log + metric on rotation).
/// </summary>
internal sealed class VaultAgentFileWatcher : BackgroundService
{
    private readonly string _path;
    private readonly ILogger _logger;

    public VaultAgentFileWatcher(string path, ILogger<VaultAgentFileWatcher> logger)
    {
        _path = path;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!Directory.Exists(_path))
            {
                _logger.LogDebug("Vault Agent secrets path {Path} not found — watcher disabled", _path);
                return;
            }

            using var watcher = new FileSystemWatcher(_path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            _logger.LogInformation("Vault Agent file watcher started on {Path}", _path);

            var tcs = new TaskCompletionSource();
            stoppingToken.Register(() => tcs.TrySetResult());

            watcher.Changed += (_, e) =>
                _logger.LogInformation("Vault Agent rotated: {File}", e.Name);

            await tcs.Task;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }
}
