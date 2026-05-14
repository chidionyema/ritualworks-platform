# Scheduler Service

## Overview

The Scheduler service owns the **deferred event scheduling** bounded context. It provides a generic mechanism to schedule any domain event for future delivery, decoupling the triggering service from time-based event dispatch.

Callers submit a `ScheduleEventCommand` specifying a future timestamp, a target RabbitMQ exchange, a routing key, and an arbitrary payload object. The service enqueues a Hangfire background job that will publish the payload to the bus at the scheduled time.

Typical use cases include:
- Sending reminder notifications at a future time
- Triggering subscription renewal checks
- Scheduling promotional event expiry

## Architecture

The service follows Clean Architecture with four layers:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Scheduler.Domain` | Reserved; no business entities currently (outbox tables only) |
| Application | `Scheduler.Application` | MediatR command, FluentValidation, `IEventScheduler` interface |
| Infrastructure | `Scheduler.Infrastructure` | Hangfire scheduler implementation, MassTransit event publisher job, RabbitMQ wiring |
| API | `Scheduler.Api` | ASP.NET Core controller, Hangfire dashboard, migration runner |

Key dependencies:
- **MediatR** — CQRS dispatch
- **FluentValidation** — command validation via `ValidationBehavior<,>` pipeline
- **Hangfire + Hangfire.PostgreSql** — durable background job storage and execution
- **MassTransit 8.x + RabbitMQ** — event publishing from within Hangfire jobs
- **EF Core 9 + Npgsql** — PostgreSQL persistence (outbox tables; no custom business entities)
- **Haworks.BuildingBlocks.Authentication** — JWKS-based JWT validation
- **Serilog** — structured logging

## Domain Model

The Scheduler service has no domain business entities. Its `SchedulerDbContext` contains only the MassTransit outbox infrastructure tables (OutboxMessage, OutboxState, InboxState).

The scheduling concept is represented by:

**`IEventScheduler`** (application interface)
```csharp
Task ScheduleEventAsync(DateTimeOffset scheduledTime, string targetExchange, string routingKey, object payload);
```

**`HangfireEventScheduler`** (infrastructure implementation)

Delegates to `IBackgroundJobClient.Schedule<EventPublisherJob>`, creating a Hangfire delayed job that runs `EventPublisherJob.PublishAsync(targetExchange, routingKey, payload)` at the specified time.

**`EventPublisherJob`** (Hangfire job)

Injects `IPublishEndpoint` (MassTransit) and publishes the payload object using the standard MassTransit publish topology. The `targetExchange` and `routingKey` parameters are logged but currently the job uses `IPublishEndpoint.Publish(payload)` which routes by type through the MassTransit exchange topology.

## API Endpoints

Base path: `/api/scheduling`

The controller does not require JWT authentication (no `[Authorize]` attribute). It is intended to be called by internal platform services, not directly by end users.

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/scheduling/schedule` | None | Schedule a future event for publication |

**POST `/api/scheduling/schedule` — request body:**
```json
{
  "scheduledTime": "2026-06-01T12:00:00Z",
  "targetExchange": "string",
  "routingKey": "string",
  "payload": { }
}
```

**Response:** `202 Accepted` (no body)

Validation rules (enforced by FluentValidation):
- `ScheduledTime`: must be in the future (greater than `DateTimeOffset.UtcNow`)
- `TargetExchange`: required, non-empty
- `RoutingKey`: required, non-empty
- `Payload`: must not be null

## Events

### Published

Any event object passed as `Payload` in the `ScheduleEventCommand` — published at the scheduled time via `IPublishEndpoint.Publish(payload)`. The service is payload-agnostic; it does not define or own any specific event contracts.

### Consumed

None. The service does not subscribe to any external events.

## Configuration

| Key | Description |
|---|---|
| `ConnectionStrings:scheduler` | PostgreSQL connection string (used by both EF Core and Hangfire storage) |
| `RabbitMq:Host` | RabbitMQ broker hostname |
| `RabbitMq:Username` | RabbitMQ username (default: `guest`) |
| `RabbitMq:Password` | RabbitMQ password (default: `guest`) |
| `Authentication:Authority` | JWKS endpoint for JWT validation (configured but endpoint has no `[Authorize]`) |

EF Core migrations are applied automatically at startup (skipped in `Test` environment).

Hangfire is configured with:
- `CompatibilityLevel.Version_180`
- `UseSimpleAssemblyNameTypeSerializer`
- `UseRecommendedSerializerSettings`
- PostgreSQL storage via `UsePostgreSqlStorage` (same connection string as EF Core)

The Hangfire dashboard is exposed at `/hangfire`.

## Database

- **Connection string key**: `scheduler`
- **Schema**: default PostgreSQL public schema

### Tables

| Table | Description |
|---|---|
| *(Hangfire tables)* | Hangfire PostgreSQL storage tables managed by the Hangfire library (Jobs, States, Queues, Sets, etc.) |
| `OutboxMessage` | MassTransit transactional outbox messages |
| `OutboxState` | Outbox processor state |

There are no custom business entity tables.

## Testing

| Project | Type | Location |
|---|---|---|
| `Scheduler.Unit` | Unit tests (xUnit + FluentAssertions) | `tests/Scheduler.Unit/` |
| `Scheduler.Integration` | Integration tests (WebApplicationFactory) | `tests/Scheduler.Integration/` |
| `Scheduler.Architecture` | Architecture tests (NetArchTest) | `tests/Scheduler.Architecture/` |

**Integration test** (`tests/Scheduler.Integration/SchedulerIntegrationTests.cs`):

`Schedule_Should_Return_Accepted_And_Enqueue_Job`
- Posts a `ScheduleEventCommand` with `ScheduledTime = UtcNow.AddDays(1)` to `POST /api/Scheduling/schedule`
- Asserts `202 Accepted`
- Verifies the mock `IBackgroundJobClient` had `Create` called with a `Job` whose method name is `PublishAsync` and state is `ScheduledState`

The integration test factory mocks `IBackgroundJobClient` directly to avoid Hangfire storage dependencies.

Any new integration tests must use `SharedTestPostgres.CreateDatabaseAsync("scheduler")` from `BuildingBlocks.Testing.Containers`.
