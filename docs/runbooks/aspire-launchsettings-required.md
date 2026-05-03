# Runbook: Aspire `AddProject<T>()` requires `Properties/launchSettings.json`

## Symptom

A new service is added to the Aspire AppHost via `builder.AddProject<Projects.<Service>_Api>("<service>-svc")`. The Aspire dashboard shows the resource state as "Running" but the service never reaches "Ready". `lsof` on the service's PID shows it has loaded all its assemblies but bound zero TCP ports. The process sits at 0% CPU.

This was observed wiring up identity-svc end-to-end on 2026-05-03 — the second silent-hang of the same session, with the same outward symptoms as the [Serilog silent-swallow](./serilog-silent-swallow.md) bug, masquerading as the same issue.

## Cause

`Aspire.Hosting.AddProject<T>()` auto-discovers the service's HTTP endpoints by reading the project's `Properties/launchSettings.json` `applicationUrl` value, then injects an `ASPNETCORE_URLS` environment variable into the launched process so Kestrel binds those exact ports.

If `Properties/launchSettings.json` does **not exist** (or has no `applicationUrl`), Aspire **silently skips** the URL injection. ASPNETCORE_URLS is never set. Kestrel falls back to its built-in defaults — `http://localhost:5000` and `https://localhost:5001`.

On macOS dev machines, **port 5000 is permanently held by `ControlCenter`** (AirPlay Receiver). Port 7000 is also held (AirDrop). Kestrel's bind attempt fails, but in dev mode Kestrel can hang on the bind retry rather than crashing — the result is a process that's loaded, blocked on a syscall, and never logs anything because the host hasn't fully started.

## How we identified it

After ruling out [Serilog silent-swallow](./serilog-silent-swallow.md) (the trace made it past `app.Run()`), looked at the process's actual environment via `ps eww -p <pid>`. Saw `ASPNETCORE_ENVIRONMENT=Development`, `OTEL_*` vars, `Vault__*` vars — but **no `ASPNETCORE_URLS`**.

Cross-checked: the standalone command `dotnet bin/.../Identity.Api.dll` with explicit `ASPNETCORE_URLS="http://localhost:5099"` worked fine. Aspire-launched did not.

Added `Properties/launchSettings.json` with explicit `applicationUrl`. Restarted Aspire. Identity-svc bound the ports. `/health` returned 200.

## Fix

Every service project MUST have `Properties/launchSettings.json` declaring its `applicationUrl`:

```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5101",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    },
    "https": {
      "commandName": "Project",
      "applicationUrl": "https://localhost:7101;http://localhost:5101",
      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

Pick a port range per service to avoid collisions:

| Service | HTTP | HTTPS |
|---|---|---|
| identity-svc | 5101 | 7101 |
| catalog-svc | 5102 | 7102 |
| orders-svc | 5103 | 7103 |
| payments-svc | 5104 | 7104 |
| content-svc | 5105 | 7105 |
| checkout-orchestrator-svc | 5106 | 7106 |
| bff-web | 5107 | 7107 |

(Aspire DCP may still re-map these to its own dashboard-friendly ports for the proxy, but the inner Kestrel will bind the launchSettings ports.)

## Why this isn't auto-detected by Aspire

`AddProject<T>()` operates on a project type that doesn't carry runtime metadata about endpoints. Without launchSettings.json, Aspire has no way to know what URL the service intends to bind. Rather than failing loud, it proceeds — leaving Kestrel to its defaults — which produces this silent hang on macOS dev boxes specifically.

Possible future improvement: a CI / pre-commit check that scans `src/<Service>/<Service>.Api/` for missing `Properties/launchSettings.json` and fails the build with a clear error.

## How to detect this in the future

If a service is in Aspire's "Running" state but never goes to "Ready":

1. Find the service's PID: `ps aux | grep <Service>.Api | grep -v grep`
2. Check listen ports: `lsof -nP -iTCP -sTCP:LISTEN -p <pid>` — if empty, Kestrel didn't bind.
3. Check env: `ps eww -p <pid> | tr ' ' '\n' | grep ASPNETCORE_URLS` — if missing, this is the bug.
4. Add `Properties/launchSettings.json` per the template above.
5. Restart Aspire.

## See also

- [`docs/CHECKPOINT.md`](../CHECKPOINT.md) — current project state
- [`docs/runbooks/serilog-silent-swallow.md`](./serilog-silent-swallow.md) — the *other* silent hang (logging gagged)
- The fix landed in commit `0f0ecaf` (`identity-svc: add launchSettings so Aspire injects ASPNETCORE_URLS`)
