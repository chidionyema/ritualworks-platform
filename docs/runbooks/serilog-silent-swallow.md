# Runbook: Serilog `ReadFrom.Configuration` silent-swallow

## Symptom

A new service appears to hang on startup. The Aspire dashboard shows the resource state as "Running" but the service never emits a log line, never reports "Now listening on", and never responds to requests. CPU sits near 0%. The process is alive but functionally dead.

This was first observed wiring up identity-svc on 2026-05-03.

## Cause

`Serilog.Settings.Configuration.ReadFrom.Configuration(IConfiguration)` is **strict** about the shape of the `Serilog` section in `appsettings.json`. If anything is malformed or missing — a wrong sink name, an unrecognized argument, a missing referenced assembly — Serilog **silently disables ALL log output** rather than falling back to defaults.

Since Serilog has hijacked the host's logging at this point (`builder.Host.UseSerilog(...)`), Microsoft's logging providers (Console, Debug) are **not active either**. Result: zero log output. Kestrel's `Microsoft.Hosting.Lifetime[14]: Now listening on:` line is swallowed along with everything else, even though Kestrel did successfully bind the port.

The startup process then either:
- runs to completion silently (all subsequent app logs vanish), or
- hangs at a step that would normally log a warning about its slow progress (e.g., HTTPS cert generation), with no indication anything is wrong.

## How we identified it

Added `Console.Error.WriteLine("[BOOT] step N done"); Console.Error.Flush();` between every line of `Program.cs`. `Console.Error` bypasses Microsoft's logging entirely and goes straight to the inherited file descriptor — so even with Serilog suppressing everything, `Console.Error` still surfaces in the captured output.

Output traced past `app.Run()`. Process kept running. No Kestrel logs. Cause confirmed.

## Fix

Always pair `ReadFrom.Configuration(...)` with **at least one explicit sink** as fallback:

```csharp
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()  // explicit fallback — guarantees logs surface
        .Enrich.FromLogContext();
});
```

The explicit `.WriteTo.Console()` sink is now redundant if appsettings is correct (Console gets registered twice → only once due to dedupe), but **guarantees** that even if appsettings is wrong, you'll still see Kestrel's `Now listening on` line and any startup errors.

## Why we don't just trust appsettings

It's not that appsettings is *wrong* per se — it's that the Serilog config schema is unforgiving and the failure mode is invisible. A perfectly valid-looking config can silently disable logging if a sink package isn't installed, or if a key name is one character off, or if `WriteTo` is a single object instead of an array. The cost of always having an explicit sink is one line of code; the cost of debugging a silent hang is hours.

## How to detect this in the future

If a service appears to hang on startup with no log output:

1. Add `Console.Error.WriteLine("[BOOT] reached <X>")` before `app.Run()`.
2. If the trace prints but no Kestrel logs appear, you are gagged by Serilog.
3. Check: do you have `WriteTo.Console()` (or another explicit sink) in your `UseSerilog` lambda?
4. If not, add it. Restart. Real logs will appear.

## See also

- [`docs/CHECKPOINT.md`](../CHECKPOINT.md) — current project state
- [`docs/runbooks/aspire-launchsettings-required.md`](./aspire-launchsettings-required.md) — the *other* silent hang (port collision)
- The fix landed in commit `4caa73d` (`identity-svc: DI wired, runs end-to-end on http://*/health → 200`)
