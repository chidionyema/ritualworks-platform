# Payments integration tests — Docker/Testcontainers flake

**Symptom:** `tests/Payments.Integration` fails with one or both of:

- `Npgsql.NpgsqlException: Exception while reading from stream → System.IO.EndOfStreamException: Attempted to read past the end of the stream` on the consumer's lookup query
- `System.ArgumentException: Docker is either not running or misconfigured` if Docker Desktop has crashed under sustained Testcontainers load

**Status (2026-05-03):** Architecture (3) + Unit (20) + Contract (3) all green. Integration tests run end-to-end against the rebuilt Postgres + EF retry-on-failure path on a healthy Docker daemon. Repeated runs occasionally hit the EOF flake; the EF retry-on-failure (5 × 500ms) inside `Payments.Infrastructure.DependencyInjection` masks it in CI but doesn't eliminate the underlying race.

## Root cause hypothesis

Two separate forces:

1. **Npgsql 9 + Testcontainers + macOS Docker Desktop**: when a DI scope is short-lived (test seed → fixture dispose → MT consumer scope), the second connection drawn from Npgsql's pool occasionally references a backend that Docker's port-forward has already torn down. Manifests as EOF stream because the TCP session is half-open.

2. **Docker Desktop instability**: heavy churn (each test spins up + tears down a postgres container) can crash Docker Desktop on macOS, requiring a manual restart from the GUI.

## Remediation (in order of severity)

| Action | When |
|---|---|
| Re-run the test suite once | Transient EOF stream during steady-state Docker |
| Restart Docker Desktop (Mac GUI) | If `docker ps` hangs or `Docker is either not running` errors appear |
| Bounce all containers: `docker rm -f $(docker ps -aq)` | If Aspire orphan containers are eating memory |
| Set `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock` | macOS Ryuk socket-mount issue (we always export this) |

## What's already wired in code

- `Payments.Infrastructure.DependencyInjection` calls `EnableRetryOnFailure(5, 500ms)` on the `UseNpgsql(...)` block — masks transient connection failures with EF's automatic retry.
- `tests/Payments.Integration/WebhookFlowsTests` sets `harness.TestTimeout = 30s, harness.TestInactivityTimeout = 10s` so the EF retry budget fits inside the harness's wait window.
- `tests/Payments.Integration/PaymentsWebAppFactory` uses `postgres:16-alpine` (smallest image) to minimize container start latency.

## What to investigate next (Phase 3+)

- Try the Npgsql `Multiplexing=true` connection string flag — single-shared-physical-connection mode that some users report sidesteps the pool/EOF interaction entirely.
- Use a single shared Testcontainers postgres for the whole assembly (`ICollectionFixture`) instead of one per fixture — cuts container churn ~75% and likely sidesteps the Docker Desktop crash.
- Pin Npgsql to 8.x if 9.x EOF behaviour proves persistent (catalog-svc uses 9.0.0 and is stable; we may have a payments-specific code path tickling the bug — likely the controller→consumer scope hop).
