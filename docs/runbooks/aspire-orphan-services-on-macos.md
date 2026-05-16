# Runbook: Aspire leaves orphan service processes on macOS

## Symptom

Several minutes into a session you start `dotnet run --project deploy/aspire/HaworksPlatform.AppHost.csproj` and everything appears to come up — Aspire dashboard reports all services Ready — but BffWeb's HttpClient calls to `catalog-svc` (or any other service) start timing out with `TaskCanceledException` from `ResolvingHttpDelegatingHandler`. The service health endpoints (`http://localhost:5050/health`, etc.) work; the per-demo endpoints that fan out to downstream services hang.

`ps -ef | grep <ServiceName>.Api` shows multiple copies of the same service — e.g. three `Catalog.Api` processes, four `BffWeb.Api`. They're orphans from prior Aspire runs.

## Cause

`Aspire.Hosting.AddProject<T>()` invokes the service via `dotnet run --no-build --project <csproj>`. That `dotnet run` is a *wrapper* that forks the actual `<Service>.Api` binary as a child:

```
dotnet run --no-build  (the wrapper)
└── /<repo>/.../bin/Debug/net9.0/<Service>.Api  (the real process)
```

When Aspire's AppHost terminates abruptly — Ctrl+C in some terminals, terminal window closure, IDE crash, `kill` without graceful shutdown, OS sleep — the macOS DCP (Aspire's Developer Control Plane) sends SIGTERM only to the *wrapper* process. The wrapper exits, but the spawned child API binary frequently does **not** receive a signal and detaches into the orphaned-process pool.

Worse: Aspire's `dcpctrl` reverse-proxy assigns a fresh dynamic port on every start, but it can route to whichever process is bound to its preferred upstream port — and that's frequently a stale orphan whose:

- DB connection pool is dead (Postgres restarted between runs)
- Vault token has expired
- RabbitMQ connection is in zombie state
- In-memory caches are stale

So requests reach an apparently-listening port and silently hang, indistinguishable from a network partition.

## How we identified it

After observing BffWeb's `Catalog chaos/trigger unreachable; falling back to local log` warnings:

```bash
$ pgrep -fl "(Catalog|Orders|Identity|Payments|CheckoutOrchestrator|BffWeb)\.Api/bin"
# 26 processes — should be 6 (one per service).
```

Each of those was anchored to a different stale port that the current Aspire run's proxy could intermittently route to.

## Fix

Use the wrapper script `scripts/aspire-up.sh` instead of invoking `dotnet run` on the AppHost directly. The wrapper:

1. **Pre-cleans** any orphan service processes from the repo's `bin/Debug` paths before starting.
2. Starts the AppHost in the foreground with `wait` (not `exec`), so the bash process survives.
3. **Forwards** SIGINT / SIGTERM / SIGHUP to the AppHost.
4. On exit (any cause), runs the same cleanup pass to kill its own children before returning.

```bash
./scripts/aspire-up.sh                # default
./scripts/aspire-up.sh --build        # build first
SKIP_PRECLEAN=1 ./scripts/aspire-up.sh  # opt out of pre-clean
```

The pattern matches only `<repo>/src/<ServiceName>/<ServiceName>.Api/bin/Debug/...`, so it cannot kill other unrelated dotnet processes that happen to share a name.

## Manual fallback

If you've already started Aspire the old way and hit this:

```bash
# 1. Stop the AppHost (Ctrl+C the dotnet run).
# 2. Clean up:
pgrep -fl "(Catalog|Orders|Identity|Payments|CheckoutOrchestrator|BffWeb)\.Api/bin/Debug" \
  | awk '{print $1}' | xargs -r kill -9
pgrep -fl "dotnet run --no-build --project $(pwd)/src" \
  | awk '{print $1}' | xargs -r kill -9
# 3. Re-run via the wrapper.
./scripts/aspire-up.sh
```

## Why not fix this in the AppHost itself?

`AppDomain.CurrentDomain.ProcessExit` only fires on the AppHost's *own* shutdown — it can't reach across to the dotnet-run wrappers' children once they've forked and detached. Aspire's DCP would need to put each service in a process group / `setsid` and group-kill on shutdown, which is a Microsoft-side change. Until then, the bash wrapper is the lowest-risk place to handle this.
