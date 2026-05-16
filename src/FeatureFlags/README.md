# FeatureFlags Service

Centralized feature flag evaluation and management. Supports percentage rollouts, region targeting, and user-level overrides with Redis-backed caching.

## Responsibilities
- Evaluate feature flags against rules (user, region, percentage rollout)
- CRUD management of flags and rules (admin-only)
- Cache flag state in Redis for low-latency evaluation
- Consume `FeatureFlagUpdated` events to invalidate cache

## API Endpoints
| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/api/featureflags/evaluate` | None | Evaluate a flag by name, optional region/user |
| POST | `/api/featureflags/update` | Admin | Create or update a flag |

## Domain Entities
- **FeatureFlag** — Id, Name (unique), Description, IsEnabled, Rules (1:N)
- **FeatureFlagRule** — Id, FeatureFlagId, UserId?, Region?, PercentageRollout?

## Events Consumed
- `FeatureFlagUpdated` — re-hydrates flag from DB, updates cache

## Events Published
- `FeatureFlagUpdated` — on flag create/update via outbox

## Infrastructure Dependencies
- PostgreSQL (`FeatureFlagsDbContext`, schema: `featureflags`)
- Redis — flag evaluation cache
- RabbitMQ via MassTransit (transactional outbox)

## Configuration
```
ConnectionStrings:featureflags    Postgres connection string
Redis:ConnectionString            Redis connection string
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<FeatureFlagsDbContext>()`
