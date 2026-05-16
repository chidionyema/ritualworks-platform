# BuildingBlocks

Shared infrastructure library referenced by every service. Provides cross-cutting concerns so services focus on domain logic, not plumbing.

## Modules

| Module | What it provides |
|--------|-----------------|
| **Common** | `Result<T>` monad, `Error` types, `ValidationException` |
| **Persistence** | `AuditableEntity` base class, `MigrateWithRetryAsync()` |
| **Messaging** | `IDomainEventPublisher`, MassTransit outbox integration, `GlobalFaultConsumer`, consumer relay pause/resume |
| **Behaviors** | MediatR pipeline: `TelemetryBehavior`, `ValidationBehavior` |
| **Authentication** | JWT configuration, JWKS validation, claims extraction, role-based auth |
| **Caching** | Distributed cache patterns (L1 in-process + L2 Redis) |
| **CurrentUser** | Request-scoped user context from JWT claims |
| **Idempotency** | Outbox-based idempotent request handling |
| **Middleware** | Authentication middleware, global error handling |
| **Resilience** | Polly policies (retry, circuit breaker, bulkhead) for HTTP clients |
| **Startup** | `AddServiceDefaults()` — one-call setup for logging, health checks, resilience, OpenTelemetry |
| **Telemetry** | OpenTelemetry wiring (traces, metrics, exporters) |
| **Vault** | HashiCorp Vault AppRole auth, dynamic DB credential fetching and rotation |

## Key Patterns
- **`Result<T>`** — used instead of exceptions for expected failures across all application layers
- **Transactional outbox** — MassTransit EF Core outbox ensures events are published exactly once per DB transaction
- **MediatR behaviors** — cross-cutting telemetry and validation run automatically on every command/query
- **Vault rotation** — dynamic Postgres credentials fetched at startup and rotated on TTL expiry

## Usage
Every service references BuildingBlocks and calls `AddServiceDefaults()` in its DI setup:
```csharp
builder.Services.AddServiceDefaults(builder.Configuration, builder.Environment);
```

## Dependencies
MassTransit 8, MediatR 12, FluentValidation 11, Polly 8, VaultSharp, OpenTelemetry, Serilog, EF Core 9, Npgsql.
