# Load Tests

k6 load tests for validating SLOs under realistic and stress conditions.

## Prerequisites

```bash
brew install k6   # macOS
# or: https://k6.io/docs/getting-started/installation/
```

## Tests

| Script | What it tests | SLO |
|--------|--------------|-----|
| `checkout-flow.js` | Full checkout saga (browse → reserve → confirm → checkout) | 99.9% success, P99 < 30s |
| `media-upload.js` | Presigned URL generation throughput | 99% success, P99 < 2s |

## Running

### Against local (docker-compose)

```bash
# Start the platform
cd deploy/compose && docker compose up -d

# Run checkout flow (ramp to 100 VUs)
k6 run tests/load/checkout-flow.js

# Run media upload (20 req/s sustained)
k6 run tests/load/media-upload.js
```

### Against Fly (production)

```bash
k6 run -e BASE_URL=https://haworks-bffweb.fly.dev \
       -e SERVICE_SECRET=your-service-secret \
       tests/load/checkout-flow.js
```

### With Grafana Cloud k6

```bash
K6_CLOUD_TOKEN=your-token k6 cloud tests/load/checkout-flow.js
```

## Interpreting Results

k6 will abort with exit code 99 if any threshold is breached:
- `checkout_success_rate > 0.999` — SLO violation
- `checkout_e2e_duration p(99) < 30000` — latency SLO
- `http_req_failed < 0.01` — infrastructure error rate

## Tuning Connection Pooling

After load testing, tune Npgsql pool sizes based on observed connection counts:

```bash
# Check active connections per service in Prometheus:
# sum(pg_stat_activity_count) by (datname)

# If connections hit MaxPoolSize (default 100 in Npgsql):
# Add to connection string: "Maximum Pool Size=200;Minimum Pool Size=10"
```
