# Runbook: production OTLP endpoint as a Fly secret

## What

Every .NET app on Fly (`bff-web`, `catalog-svc`, `checkout-svc`, `content-svc`,
`identity-svc`, `notifications-svc`, `orders-svc`, `payments-svc`, `search-svc`,
`audit-svc`) needs `OTEL_EXPORTER_OTLP_ENDPOINT` set as a **Fly secret**, not
as a toml `[env]` value. Without it, OpenTelemetry auto-instrumentation runs
but every span and metric is dropped at export time. No traces reach the
backend; no errors are logged.

Apply it once per environment with the helper script:

```bash
./scripts/fly-set-otel-endpoint.sh https://your-otlp-collector.example:443
```

Static OTel identity attributes (`OTEL_SERVICE_NAME`,
`OTEL_RESOURCE_ATTRIBUTES`) are **already** in each `fly.<svc>.toml` `[env]`
block, committed in `07cce61`. Only the endpoint URL is provisioned out-of-band.

## Why a secret and not a committed value

Two reasons, in order of weight:

1. **Fly env values do not interpolate `${VAR}`.** A toml `[env]` block is a
   literal string map. We cannot write `OTEL_EXPORTER_OTLP_ENDPOINT =
   "${TEMPO_URL}"` and expect Fly to expand it from a secret at deploy time —
   it would ship the literal string `${TEMPO_URL}` to the container.
   Per-environment URLs therefore must be provisioned through a path that does
   resolve, and `fly secrets set` is that path.
2. **One source of truth per environment.** The dev stack points OTel at the
   Aspire dashboard's collector on `localhost:4317`. Staging points at a
   self-hosted Tempo. Production points at Grafana Cloud / Honeycomb / wherever.
   Treating the URL as a secret keeps the toml identical across environments
   and makes the swap a one-line operation.

The URL is not really sensitive — leaking a Tempo ingress URL is not a breach
— but treating it like a secret is the cleanest mechanism Fly offers for
per-environment values, so we use it.

## How — first setup

Once per Fly organization / environment. Pre-requisites:

- `flyctl auth login` already done as someone with deploy rights to the
  `haworks-*` apps.
- The OTLP backend already exists and you have its ingestion URL (see
  *Choosing an OTLP backend* below).

```bash
./scripts/fly-set-otel-endpoint.sh https://tempo-prod-04-prod-us-east-0.grafana.net:443
```

What this does:

- Validates the URL has an `http://` or `https://` scheme.
- Loops the 10 .NET apps and runs `fly secrets set
  OTEL_EXPORTER_OTLP_ENDPOINT=<url> -a <app>` for each.
- Prints an `OK` / `FAIL` summary table at the end.

Each `fly secrets set` triggers a rolling redeploy of that app's machines —
expect ~30 seconds per app while the new env propagates. The script does not
wait for redeploys; it returns as soon as Fly accepts the secret.

If a single app fails (e.g. you have not yet provisioned it), the script
**continues** with the rest and exits non-zero at the end. Re-run after
fixing.

## How — rotation

Re-run the same command with the new URL:

```bash
./scripts/fly-set-otel-endpoint.sh https://api.honeycomb.io:443
```

Fly diff-updates the secret and rolls each app's machines. There is no
"unset old" step — the secret is overwritten in place.

If you only need to rotate one app (rare, but possible during backend
migration testing):

```bash
flyctl secrets set OTEL_EXPORTER_OTLP_ENDPOINT="https://new-url:443" -a haworks-bffweb
```

## Verifying

After the script completes, pick any app and confirm both the static
identity attrs and the secret-injected URL are visible inside the container:

```bash
fly ssh console -a haworks-bffweb -C "env | grep OTEL"
```

Expected output:

```
OTEL_SERVICE_NAME=bff-web
OTEL_RESOURCE_ATTRIBUTES=deployment.environment=production,service.namespace=haworks
OTEL_EXPORTER_OTLP_ENDPOINT=https://tempo-prod-04-prod-us-east-0.grafana.net:443
```

Then hit the app and watch a trace land in your backend's UI:

```bash
curl -fsS https://haworks-bffweb.fly.dev/health
```

The `GET /health` request should appear as a span in Tempo / Honeycomb /
whatever within ~10 seconds. If it does not, see *What if I forget?* below.

## Choosing an OTLP backend

Whichever you pick, the URL goes into the secret — no code changes, no toml
changes.

| Backend                     | Cost                       | Setup time | Notes                                                                 |
| --------------------------- | -------------------------- | ---------- | --------------------------------------------------------------------- |
| **Grafana Cloud Tempo**     | Free tier 50 GB/mo         | ~15 min    | Fastest path. Sign up, copy the OTLP gRPC endpoint, paste into script. |
| **Self-hosted Tempo on Fly**| ~$5/mo VM + volume         | ~half day  | More control (PII filtering, retention knobs). More ops surface — Tempo + S3-or-volume + auth in front. |
| **Honeycomb / New Relic / Datadog** | Commercial, $20+/mo  | ~30 min    | Best UI / query experience. Per-event pricing scales with traffic.     |

For this platform's current scale (one prod region, low five-figure RPM),
Grafana Cloud's free tier is the obvious starting point. Migrate when either
the free tier is exhausted or query latency becomes a sore spot.

## What if I forget?

Symptoms of "forgot to set the secret":

- All apps boot fine, `/health` returns 200.
- No traces appear in your backend after a deploy.
- `fly logs -a haworks-bffweb` shows zero lines containing `OTLP`,
  `Exporter`, or `BatchExportProcessor`. The OTel SDK is loaded but the
  exporter is silently no-op'ing because it has no endpoint configured.
- App logs themselves are unaffected (Serilog still writes to stdout).

**Why no error is logged:** the OTel exporter treats a missing endpoint
the same as a transient network failure — it queues batches, retries with
backoff, drops them. Drops are counted by an internal metric but nothing is
logged at warn or error level by default. This is a deliberate SDK design
choice (so a flaky collector doesn't drown your app logs in noise) and it
also means a misconfiguration is invisible.

To detect:

1. `fly ssh console -a haworks-bffweb -C "env | grep OTEL_EXPORTER"` —
   if `OTEL_EXPORTER_OTLP_ENDPOINT` is missing or empty, this is the bug.
2. Run `./scripts/fly-set-otel-endpoint.sh <url>` to fix.
3. Wait for the rolling redeploy (~30s) and re-test with `curl /health`.

If the secret IS set but traces still aren't landing, the next things to
check (in order):

- Backend auth header. Some collectors need
  `OTEL_EXPORTER_OTLP_HEADERS=api-key=...` as a separate secret.
- URL scheme. gRPC OTLP uses port 4317; HTTP/protobuf uses 4318. Mixing
  the two manifests as connection-refused or 404, both visible in
  `fly logs`.
- Network egress. Self-hosted Tempo on a private Fly app must be reached
  via `*.flycast` from inside the Fly network, not the public DNS.

## See also

- [`docs/runbooks/serilog-silent-swallow.md`](./serilog-silent-swallow.md) —
  another silent-failure mode with the same root cause shape (component
  swallows its own errors).
- [`docs/runbooks/search-service-deployment.md`](./search-service-deployment.md)
  — example of using Fly secrets for a related per-environment value
  (`MEILI_MASTER_KEY`).
- The static OTel identity attrs landed in commit `07cce61`
  (`feat(fly/observability): wire OTEL service identity to all production app
  fly configs`).
- Helper: [`scripts/fly-set-otel-endpoint.sh`](../../scripts/fly-set-otel-endpoint.sh)
