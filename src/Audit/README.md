# Audit Service

Append-only audit log for all domain events across the platform. Provides queryable, exportable event history with GDPR-safe redaction.

## Responsibilities
- Consume every `IDomainEvent` via a generic MassTransit consumer
- Redact PII fields before persistence
- Expose cursor-paginated query and async export APIs

## API Endpoints
| Method | Route | Auth |
|--------|-------|------|
| GET | `/audit/events` | `audit-reader` |
| GET | `/audit/events/{id}` | `audit-reader` |
| POST | `/audit/export` | `audit-admin` |
| GET | `/audit/export/{jobId}` | `audit-admin` |

## Domain Entities
- **AuditEvent** — append-only; fields: `EventType`, `EntityType`, `EntityId`, `ActorId`, `Payload` (JsonDocument), `Metadata` (JsonDocument)

## Events Consumed
- Any `IDomainEvent` (generic consumer `AuditConsumer<T>`)

## Events Published
None — write-only sink.

## Infrastructure Dependencies
- PostgreSQL (`AuditDbContext`)
- RabbitMQ via MassTransit (`AuditMassTransit.RegisterConsumers()`)

## Configuration
```
ConnectionStrings:audit       Postgres connection string
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<AuditDbContext>()`
