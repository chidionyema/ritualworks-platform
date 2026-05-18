using System.Collections.Concurrent;
using System.Diagnostics;
using Haworks.BffWeb.Api.SignalR;
using Haworks.BuildingBlocks.Middleware;
using Microsoft.AspNetCore.SignalR;

namespace Haworks.BffWeb.Api.Demo;

/// <summary>
/// Singleton in-memory ring buffer + SignalR fan-out for the live console
/// dock. Captures the last <see cref="BufferSize"/> events so a freshly-
/// connected client can backfill recent activity (otherwise the dock looks
/// empty until the visitor presses a button).
///
/// MUST be Singleton: the IHubContext consumer is Singleton-safe per
/// SignalR's own DI, and the ring buffer needs a single instance to work.
///
/// Threading: writes to the ring use a single Interlocked counter + slot
/// array. Reads (snapshot for backfill) take a lock briefly to copy the
/// active range. Fan-out to clients runs on a background task — we never
/// want a slow SignalR connection to add latency to the actual HTTP request
/// being recorded.
/// </summary>
public sealed class LiveConsoleBroadcaster
{
    private const int BufferSize = 200;

    private readonly LiveConsoleEvent?[] _ring = new LiveConsoleEvent?[BufferSize];
    private long _writeIndex;
    private readonly Lock _readLock = new();

    private readonly IHubContext<LiveConsoleHub> _hub;
    private readonly ILogger<LiveConsoleBroadcaster> _logger;
    private readonly LiveConsoleHello _hello;

    public LiveConsoleBroadcaster(
        IHubContext<LiveConsoleHub> hub,
        ILogger<LiveConsoleBroadcaster> logger)
    {
        _hub = hub;
        _logger = logger;
        _hello = new LiveConsoleHello(
            Service: "bff-web",
            InstanceId: InstanceIdMiddleware.InstanceId,
            GitSha: ReadGitSha(_logger),
            ProcessStartedAt: Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o"));
    }

    /// <summary>
    /// Build identity sent on every hub connect. Computed once at process
    /// start; the values don't change for the lifetime of the process.
    /// </summary>
    public LiveConsoleHello Hello => _hello;

    private static string ReadGitSha(ILogger logger)
    {
        // Prefer an env var if the orchestrator set one (aspire-up.sh
        // can populate this without forking processes per service).
        var envSha = Environment.GetEnvironmentVariable("GIT_SHA");
        if (!string.IsNullOrWhiteSpace(envSha))
        {
            return envSha.Length > 12 ? envSha[..12] : envSha;
        }

        // Fall back to invoking git on the working directory. Best-effort;
        // returns "unknown" on any failure (no git, detached worktree, etc).
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "unknown";
            if (!p.WaitForExit(2000)) { p.Kill(); return "unknown"; }
            var sha = p.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrEmpty(sha) ? "unknown" : sha;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(ReadGitSha));
            return "unknown";
        }
    }

    /// <summary>
    /// Append an event to the ring and fan out to every connected client.
    /// Fire-and-forget — caller should not await broadcast on the hot path.
    /// </summary>
    public void Emit(LiveConsoleEvent ev)
    {
        var slot = (int)((Interlocked.Increment(ref _writeIndex) - 1) % BufferSize);
        _ring[slot] = ev;

        // Fan out on a thread-pool task so a stuck WebSocket can't slow
        // the HTTP pipeline. Errors are logged and swallowed — the ring
        // keeps recording even if SignalR is having a bad day.
        _ = Task.Run(async () =>
        {
            try
            {
                await _hub.Clients.All.SendAsync("OnConsoleEvent", ev);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Live console fan-out failed (non-fatal)");
            }
        });
    }

    /// <summary>
    /// Snapshot of the last N events in chronological order (oldest first).
    /// Used by the hub on client connect to backfill the dock so a visitor
    /// sees recent activity instead of an empty list.
    /// </summary>
    public IReadOnlyList<LiveConsoleEvent> GetRecent(int max = BufferSize)
    {
        lock (_readLock)
        {
            var written = Interlocked.Read(ref _writeIndex);
            if (written == 0) return Array.Empty<LiveConsoleEvent>();

            var count = (int)Math.Min(written, BufferSize);
            count = Math.Min(count, max);

            var result = new List<LiveConsoleEvent>(count);
            // Walk the ring in chronological order.
            var start = written - count;
            for (var i = 0; i < count; i++)
            {
                var slot = (int)((start + i) % BufferSize);
                var ev = _ring[slot];
                if (ev is not null) result.Add(ev);
            }
            return result;
        }
    }
}
