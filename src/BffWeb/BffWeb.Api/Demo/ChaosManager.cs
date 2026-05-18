using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Haworks.BffWeb.Api.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace Haworks.BffWeb.Api.Demo;

/// <summary>
/// Local-dev chaos engineering for the topology map.
///
/// Lets a visitor click a node in the live cluster topology and "pause"
/// the underlying service so other demos genuinely fail / route around
/// the outage. Pause is implemented two ways depending on what's actually
/// running for that target:
///
/// <list type="bullet">
///   <item><b>Process targets</b> (catalog, orders, payments, checkout,
///         identity) — found by name, paused with <c>SIGSTOP</c>.</item>
///   <item><b>Container targets</b> (postgres, redis, rabbitmq, vault) —
///         found by docker container-name prefix, paused with
///         <c>docker pause</c>.</item>
/// </list>
///
/// Every pause is auto-resumed after a configurable duration (default 30s)
/// so a forgotten click can't break the dev cluster permanently. The BFF
/// itself is never a valid target; pausing it would kill the chaos API
/// itself. Wired only when <c>IsDevelopment()</c>; production registrations
/// no-op.
/// </summary>
public sealed class ChaosManager : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(2);

    private readonly IHubContext<LiveConsoleHub> _hub;
    private readonly ILogger<ChaosManager> _logger;
    private readonly ConcurrentDictionary<string, PausedState> _paused = new();
    private readonly Dictionary<string, IChaosStrategy> _strategies;

    public ChaosManager(IHubContext<LiveConsoleHub> hub, ILogger<ChaosManager> logger)
    {
        _hub = hub;
        _logger = logger;

        // Service strategies: BFF-side fault injection (the
        // FaultInjectionStrategy flips a flag the outbound DelegatingHandler
        // reads on every call). Process-level kill -STOP was safer in
        // theory but causes .NET 9 socket exceptions on resume — see
        // https://github.com/dotnet/runtime issue with IPEndPoint.Create
        // mid-accept. Visitor sees identical effect (red node + 503s).
        // Container strategies stay container-level — docker pause is safe.
        _strategies = new Dictionary<string, IChaosStrategy>
        {
            ["catalog"] = new FaultInjectionStrategy(_injected, "catalog-svc"),
            ["orders"] = new FaultInjectionStrategy(_injected, "orders-svc"),
            ["payments"] = new FaultInjectionStrategy(_injected, "payments-svc"),
            ["checkout"] = new FaultInjectionStrategy(_injected, "checkout-svc"),
            ["identity"] = new FaultInjectionStrategy(_injected, "identity-svc"),
            // Container strategies — name-prefix-matched in `docker ps`.
            // The pact-db container shares the postgres image so we filter
            // strictly by the leading `postgres-` (no `-db`) prefix.
            ["postgres"] = new ContainerStrategy("postgres-", excludeContains: "pact"),
            ["redis"] = new ContainerStrategy("redis-", excludeContains: "commander"),
            ["rabbitmq"] = new ContainerStrategy("rabbitmq-"),
            ["vault"] = new ContainerStrategy("vault-"),
        };
    }

    /// <summary>
    /// Set of upstream service names currently fault-injected. Read by
    /// the BFF's <c>ChaosFaultInjectionHandler</c> (a DelegatingHandler on
    /// every named HttpClient) on every outbound call. Membership is
    /// added/removed by <see cref="FaultInjectionStrategy"/>.
    /// </summary>
    private readonly HashSet<string> _injected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True iff outbound calls to <paramref name="serviceName"/> (e.g.
    /// "catalog-svc") should be short-circuited to 503. Thread-safe via
    /// lock.
    /// </summary>
    public bool IsServiceInjected(string serviceName)
    {
        lock (_injected)
        {
            return _injected.Contains(serviceName);
        }
    }

    public IReadOnlyCollection<string> KnownTargets => _strategies.Keys;

    public IReadOnlyDictionary<string, ChaosState> Snapshot()
    {
        var now = DateTime.UtcNow;
        var dict = new Dictionary<string, ChaosState>(_strategies.Count);
        foreach (var key in _strategies.Keys)
        {
            if (_paused.TryGetValue(key, out var state))
            {
                var remaining = state.ResumeAtUtc - now;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                dict[key] = new ChaosState(
                    Target: key,
                    Status: "paused",
                    ResumeAtUtc: state.ResumeAtUtc.ToString("o"),
                    RemainingSeconds: (int)Math.Ceiling(remaining.TotalSeconds));
            }
            else
            {
                dict[key] = new ChaosState(
                    Target: key,
                    Status: "running",
                    ResumeAtUtc: null,
                    RemainingSeconds: null);
            }
        }
        return dict;
    }

    public async Task<PauseResult> PauseAsync(
        string target,
        TimeSpan? duration,
        CancellationToken ct = default)
    {
        if (!_strategies.TryGetValue(target, out var strategy))
        {
            return PauseResult.NotFound;
        }

        var dur = duration ?? DefaultDuration;
        if (dur > MaxDuration) dur = MaxDuration;
        if (dur < TimeSpan.FromSeconds(5)) dur = TimeSpan.FromSeconds(5);

        // Idempotent: if already paused, refresh the auto-resume deadline.
        if (_paused.TryGetValue(target, out var existing))
        {
            existing.CancelAutoResume(_logger);
            var newDeadline = DateTime.UtcNow.Add(dur);
            existing.ResumeAtUtc = newDeadline;
            existing.ScheduleAutoResume(this, target, dur);
            await BroadcastAsync(ct).ConfigureAwait(false);
            return PauseResult.Ok;
        }

        try
        {
            await strategy.PauseAsync(_logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chaos pause failed for {Target}", target);
            return PauseResult.Error;
        }

        var state = new PausedState
        {
            Target = target,
            ResumeAtUtc = DateTime.UtcNow.Add(dur),
        };
        state.ScheduleAutoResume(this, target, dur);
        _paused[target] = state;
        _logger.LogInformation(
            "Chaos: paused {Target} for {Seconds}s (auto-resume at {ResumeAt})",
            target, (int)dur.TotalSeconds, state.ResumeAtUtc);
        await BroadcastAsync(ct).ConfigureAwait(false);
        return PauseResult.Ok;
    }

    public async Task<bool> ResumeAsync(string target, CancellationToken ct = default)
    {
        if (!_strategies.TryGetValue(target, out var strategy)) return false;
        if (!_paused.TryRemove(target, out var state)) return false;

        state.CancelAutoResume(_logger);
        try
        {
            await strategy.ResumeAsync(_logger, ct).ConfigureAwait(false);
            _logger.LogInformation("Chaos: resumed {Target}", target);
        }
        catch (Exception ex)
        {
            // Even if the resume call fails, drop the tracking entry — the
            // operator will need to manually intervene at this point.
            _logger.LogError(ex, "Chaos resume failed for {Target}", target);
        }
        await BroadcastAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task BroadcastAsync(CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("OnChaosState", Snapshot(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chaos state broadcast failed (non-fatal)");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Best-effort: try to resume anything still paused on shutdown.
        foreach (var key in _paused.Keys.ToArray())
        {
            try
            {
                await ResumeAsync(key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(DisposeAsync));
            }
        }
    }

    private sealed class PausedState
    {
        public required string Target { get; init; }
        public DateTime ResumeAtUtc { get; set; }
        private CancellationTokenSource? _cts;

        public void ScheduleAutoResume(ChaosManager mgr, string target, TimeSpan after)
        {
            CancelAutoResume(mgr._logger);
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(after, cts.Token).ConfigureAwait(false);
                    await mgr.ResumeAsync(target, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Replaced by a newer pause; ignore.
                }
            });
        }

        public void CancelAutoResume(ILogger logger)
        {
            try { _cts?.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(CancelAutoResume)); }
            _cts = null;
        }
    }
}

public enum PauseResult { Ok, NotFound, Error }

public sealed record ChaosState(
    string Target,
    string Status,
    string? ResumeAtUtc,
    int? RemainingSeconds);

internal interface IChaosStrategy
{
    Task PauseAsync(ILogger logger, CancellationToken ct);
    Task ResumeAsync(ILogger logger, CancellationToken ct);
}

/// <summary>
/// BFF-side fault injection: adds/removes the upstream service name from a
/// shared <see cref="HashSet{T}"/> that <c>ChaosFaultInjectionHandler</c>
/// (the DelegatingHandler on every named HttpClient) consults on every
/// outbound call. While "paused", every BFF→service request short-circuits
/// to a synthetic 503 before the network call is made — visitor sees demos
/// fail with realistic timing, the upstream service is otherwise untouched
/// (no process crash on resume, like SIGSTOP would cause).
/// </summary>
internal sealed class FaultInjectionStrategy : IChaosStrategy
{
    private readonly HashSet<string> _injected;
    private readonly string _serviceName;

    public FaultInjectionStrategy(HashSet<string> injected, string serviceName)
    {
        _injected = injected;
        _serviceName = serviceName;
    }

    public Task PauseAsync(ILogger logger, CancellationToken ct)
    {
        lock (_injected) { _injected.Add(_serviceName); }
        logger.LogDebug("Chaos: fault-injecting {Service}", _serviceName);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(ILogger logger, CancellationToken ct)
    {
        lock (_injected) { _injected.Remove(_serviceName); }
        logger.LogDebug("Chaos: clearing fault-injection for {Service}", _serviceName);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Legacy process-pause strategy. Kept for reference but not registered —
/// SIGSTOP/SIGCONT crashes .NET 9 HTTP services on resume due to a socket
/// accept-mid-pause bug.
/// </summary>
internal sealed class ProcessStrategy : IChaosStrategy
{
    private readonly string _pathFragment;

    public ProcessStrategy(string pathFragment)
    {
        _pathFragment = pathFragment;
    }

    public Task PauseAsync(ILogger logger, CancellationToken ct) =>
        SendSignalAsync(logger, "STOP", ct);

    public Task ResumeAsync(ILogger logger, CancellationToken ct) =>
        SendSignalAsync(logger, "CONT", ct);

    private async Task SendSignalAsync(ILogger logger, string signal, CancellationToken ct)
    {
        // We can't always read another process's MainModule.FileName on
        // macOS (sandbox), so shell out to ps + grep. Returns one PID per
        // matching replica (multiple if WithReplicas > 1).
        var pids = await GetPidsAsync(ct).ConfigureAwait(false);
        if (pids.Count == 0)
        {
            throw new InvalidOperationException(
                $"No process found matching '{_pathFragment}'");
        }
        foreach (var pid in pids)
        {
            await RunAsync("kill", $"-{signal} {pid}", ct).ConfigureAwait(false);
            logger.LogDebug("Chaos: kill -{Signal} {Pid}", signal, pid);
        }
    }

    private async Task<List<int>> GetPidsAsync(CancellationToken ct)
    {
        var (_, stdout, _) = await RunAsync(
            "/bin/bash",
            $"-c \"ps -eo pid,command | grep '{_pathFragment}' | grep -v grep | awk '{{print $1}}'\"",
            ct).ConfigureAwait(false);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => int.TryParse(s.Trim(), out _))
            .Select(s => int.Parse(s.Trim()))
            .ToList();
    }

    internal static async Task<(int code, string stdout, string stderr)> RunAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}

/// <summary>
/// Pauses a docker container discovered by name prefix, optionally
/// excluding any container whose name contains a substring (used to skip
/// pact-broker / redis-commander variants of the same image).
/// </summary>
internal sealed class ContainerStrategy : IChaosStrategy
{
    private readonly string _namePrefix;
    private readonly string? _excludeContains;

    public ContainerStrategy(string namePrefix, string? excludeContains = null)
    {
        _namePrefix = namePrefix;
        _excludeContains = excludeContains;
    }

    public Task PauseAsync(ILogger logger, CancellationToken ct) =>
        DockerActionAsync(logger, "pause", ct);

    public Task ResumeAsync(ILogger logger, CancellationToken ct) =>
        DockerActionAsync(logger, "unpause", ct);

    private async Task DockerActionAsync(ILogger logger, string action, CancellationToken ct)
    {
        var containers = await ResolveContainerNamesAsync(ct).ConfigureAwait(false);
        if (containers.Count == 0)
        {
            throw new InvalidOperationException(
                $"No running container found matching '{_namePrefix}*'");
        }
        foreach (var name in containers)
        {
            await ProcessStrategy.RunAsync("docker", $"{action} {name}", ct).ConfigureAwait(false);
            logger.LogDebug("Chaos: docker {Action} {Container}", action, name);
        }
    }

    private async Task<List<string>> ResolveContainerNamesAsync(CancellationToken ct)
    {
        var (_, stdout, _) = await ProcessStrategy.RunAsync(
            "docker", "ps --format {{.Names}}", ct).ConfigureAwait(false);
        var prefix = _namePrefix;
        var names = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim())
            .Where(n => Regex.IsMatch(n, $"^{Regex.Escape(prefix)}[a-z0-9]+$", RegexOptions.NonBacktracking))
            .Where(n => string.IsNullOrEmpty(_excludeContains)
                        || !n.Contains(_excludeContains, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return names;
    }
}
