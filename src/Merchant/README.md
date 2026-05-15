# Merchant Service

Manages merchant profiles: onboarding, activation, and suspension.

## Responsibilities
- Create and persist merchant profiles with slug generation
- Activate or suspend merchants (`MerchantProfile.Activate()` / `Suspend()`)
- Publish domain events via MassTransit transactional outbox

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| POST | `/api/merchants` | Create merchant profile |

## Domain Entities
- **MerchantProfile** — `OwnerId`, `Name`, `Slug`, `Bio`, `Status`; methods `Activate()` / `Suspend()`

## Events Published
- `MerchantCreatedEvent`
- `MerchantActivatedEvent`
- `MerchantSuspendedEvent`

## Infrastructure Dependencies
- PostgreSQL (`MerchantDbContext`)
- RabbitMQ via MassTransit (transactional outbox; skipped in `Test` environment)

## Configuration
```
ConnectionStrings:merchant
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<MerchantDbContext>()`
