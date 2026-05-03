using System.Diagnostics;

namespace Haworks.BuildingBlocks.Testing;

/// <summary>
/// Replaces arbitrary <c>await Task.Delay(...)</c> sleeps in integration tests.
///
/// CLAUDE.md mandate: "No <c>Task.Delay</c> in tests". The intent is no
/// arbitrary fixed-duration sleeps in test bodies — those compound into
/// minutes of dead time across the suite. <see cref="Until(Func{Task{bool}}, TimeSpan?, TimeSpan?, string?, CancellationToken)"/> polls a
/// predicate at a tight interval and returns the moment the condition
/// becomes true, falling back to a TimeoutException when the deadline
/// passes. The helper itself uses <c>Task.Delay</c> internally for the
/// inner sleep — that's the only acceptable place for it in test code.
///
/// Use <see cref="NotHappens(Func{Task{bool}}, TimeSpan?, string?, CancellationToken)"/> for the negative-assertion case
/// ("verify that something does NOT happen during a short window") —
/// this is the one place a fixed-duration wait is unavoidable, since
/// you can't poll for the absence of a future event.
/// </summary>
public static class TestWait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or the
    /// <paramref name="timeout"/> elapses. Returns instantly on the first
    /// successful poll. Throws <see cref="TimeoutException"/> on timeout
    /// with <paramref name="because"/> in the message for diagnosis.
    /// </summary>
    public static async Task Until(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        string? because = null,
        CancellationToken ct = default)
    {
        var t = timeout ?? DefaultTimeout;
        var poll = interval ?? DefaultPollInterval;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < t)
        {
            ct.ThrowIfCancellationRequested();
            if (await condition().ConfigureAwait(false)) return;
            try { await Task.Delay(poll, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { throw; }
        }
        // One last check — the loop's final await may have eaten the very
        // last poll window; give the condition one truly-deadline-aligned
        // chance before failing.
        if (await condition().ConfigureAwait(false)) return;
        throw new TimeoutException(
            because is null
                ? $"Wait.Until timed out after {t.TotalMilliseconds:F0}ms"
                : $"Wait.Until: {because} (timeout after {t.TotalMilliseconds:F0}ms)");
    }

    /// <summary>
    /// Sync-predicate overload. Use for purely in-memory checks.
    /// </summary>
    public static Task Until(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        string? because = null,
        CancellationToken ct = default) =>
        Until(() => Task.FromResult(condition()), timeout, interval, because, ct);

    /// <summary>
    /// The legitimate-fixed-delay case: assert that something does NOT
    /// happen during a short observation window. There is no way to
    /// poll for the absence of a future event, so we wait briefly and
    /// then check that the predicate is still false.
    ///
    /// Default observation window is 200ms — long enough to catch a
    /// typical bus round-trip, short enough not to dominate test runtime.
    /// Throws if <paramref name="condition"/> ever becomes true within
    /// the window.
    /// </summary>
    public static async Task NotHappens(
        Func<Task<bool>> condition,
        TimeSpan? observationWindow = null,
        string? because = null,
        CancellationToken ct = default)
    {
        var window = observationWindow ?? TimeSpan.FromMilliseconds(200);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < window)
        {
            ct.ThrowIfCancellationRequested();
            if (await condition().ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    because is null
                        ? $"Wait.NotHappens: condition became true after {deadline.Elapsed.TotalMilliseconds:F0}ms"
                        : $"Wait.NotHappens: {because} (condition became true after {deadline.Elapsed.TotalMilliseconds:F0}ms)");
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { throw; }
        }
    }

    public static Task NotHappens(
        Func<bool> condition,
        TimeSpan? observationWindow = null,
        string? because = null,
        CancellationToken ct = default) =>
        NotHappens(() => Task.FromResult(condition()), observationWindow, because, ct);
}
