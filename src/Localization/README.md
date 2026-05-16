# Localization Service

Translation string management with locale-aware lookups. Stores translations as JSONB key-value pairs per locale, with CDN integration for static asset delivery.

## Responsibilities
- Store and retrieve translations by key and locale
- Serve translations via REST for client-side i18n
- Push updated translation bundles to CDN (production) or mock (dev/test)

## API Endpoints
| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/api/translations/{key}` | None | Get translation by key, optional `?locale=en-US` |

## Domain Entities
- **Translation** — Id, Key (unique), Values (JSONB dict of locale → string)

## Events Consumed
- `TranslationUpdated` — via MassTransit consumer

## Events Published
None.

## Infrastructure Dependencies
- PostgreSQL (`LocalizationDbContext`, schema: `localization`)
- RabbitMQ via MassTransit (transactional outbox)
- CDN service (`ICdnService` — mock in dev/test, real in production)

## Configuration
```
ConnectionStrings:localization    Postgres connection string
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<LocalizationDbContext>()`
