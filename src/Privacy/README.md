# Privacy Service

## Overview

The Privacy service owns the **GDPR data subject rights** bounded context. It is responsible for:

- Accepting erasure and data export requests from authenticated users
- Persisting the request and its per-service step tracking records
- Orchestrating cross-service erasure coordination via a **MassTransit saga state machine** (`PrivacyRequestStateMachine`)
- Completing only when all dependent services (Identity, Orders) confirm erasure

The service implements GDPR Articles 17 (right to erasure) and 20 (right to data portability). It does not perform the erasure itself — it coordinates downstream services by publishing `PrivacyErasureRequested` and waits for `PrivacyErasureCompleted` acknowledgements from each.

## Architecture

The service follows Clean Architecture with four layers:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Privacy.Domain` | Aggregates, enums, domain logic |
| Application | `Privacy.Application` | MediatR commands, FluentValidation, MassTransit saga state machine |
| Infrastructure | `Privacy.Infrastructure` | EF Core persistence, saga EF repository, MassTransit/RabbitMQ wiring, outbox |
| API | `Privacy.Api` | ASP.NET Core controllers, Swagger, JWT auth, migration runner |

Key dependencies:
- **MediatR** — CQRS dispatch for command handling
- **FluentValidation** — command validation via pipeline behavior
- **MassTransit 8.x + RabbitMQ** — event bus; saga stored via `EntityFrameworkRepository`
- **MassTransit Saga State Machine** — `PrivacyRequestStateMachine` with EF Core-backed saga persistence
- **EF Core 9 + Npgsql** — PostgreSQL persistence; saga state stored in `PrivacyDbContext`
- **Haworks.BuildingBlocks.Authentication** — JWKS-based JWT validation
- **Serilog** — structured logging

## Domain Model

### Aggregates / Entities

**`PrivacyRequest`** (aggregate root, extends `AuditableEntity`)

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key; used as saga `CorrelationId` |
| `UserId` | `Guid` | The user whose data is being acted on |
| `Type` | `PrivacyRequestType` | `Export` or `Erasure` |
| `Status` | `PrivacyRequestStatus` | Current lifecycle state |
| `ContentId` | `Guid?` | Reference to exported ZIP file in Content service (Export requests only) |
| `CompletedAt` | `DateTimeOffset?` | Timestamp when the request was fully resolved |

Domain methods: `Start()`, `Complete(contentId?)`, `Fail()`

**`PrivacyRequestStep`** (entity, extends `AuditableEntity`)

Tracks the per-service completion status of a given privacy request. One row per (request, service).

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `RequestId` | `Guid` | FK to `PrivacyRequest` |
| `ServiceName` | `string` | Name of the downstream service (e.g. `"identity-svc"`, `"orders-svc"`) |
| `Status` | `PrivacyRequestStatus` | `Pending`, `Completed`, or `Failed` |
| `ErrorMessage` | `string?` | Populated on failure |
| `CompletedAt` | `DateTimeOffset?` | When this step completed |

Domain methods: `Complete()`, `Fail(message)`

### Saga State: `PrivacyRequestState`

The MassTransit saga persists correlation and step-completion flags:

| Property | Type | Description |
|---|---|---|
| `CorrelationId` | `Guid` | Matches `PrivacyRequest.Id` |
| `CurrentState` | `string` | MassTransit state machine state (`Initial`, `Processing`, `Completed`) |
| `Version` | `int` | Optimistic concurrency token |
| `UserId` | `Guid` | The user whose data is being processed |
| `IdentityCompleted` | `bool` | Whether `identity-svc` has acknowledged |
| `OrdersCompleted` | `bool` | Whether `orders-svc` has acknowledged |
| `PaymentsCompleted` | `bool` | Whether `payments-svc` has acknowledged (tracked but not yet gating completion) |
| `CreatedAt` | `DateTime?` | Saga creation timestamp |
| `CompletedAt` | `DateTime?` | Saga completion timestamp |

### Enums

**`PrivacyRequestType`**: `Export`, `Erasure`

**`PrivacyRequestStatus`**: `Pending`, `InProgress`, `Completed`, `Failed`

## API Endpoints

Base path: `/api/privacyrequests`

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/privacyrequests` | JWT required | Initiate a privacy request (erasure or export) |

**POST `/api/privacyrequests` — request body:**
```json
{
  "userId": "guid",
  "type": 0
}
```
`type`: `0` = Export, `1` = Erasure

**Response:**
```json
{ "requestId": "guid" }
```

Validation rules:
- `UserId`: required, non-empty GUID
- `Type`: must be a valid `PrivacyRequestType` enum value

## Events

### Published

| Event | When | Contract |
|---|---|---|
| `InitiatePrivacyRequestMessage` | When a request is created (triggers saga) | Internal; `(RequestId, UserId)` |
| `PrivacyErasureRequested` | By the saga on entering `Processing` state | `Haworks.Contracts.Privacy.PrivacyErasureRequested` — `(RequestId, UserId)` |

### Consumed

| Event | Source | Action |
|---|---|---|
| `PrivacyErasureCompleted` | Identity service, Orders service | Saga records completion flag per `ServiceName`; transitions to `Completed` when `IdentityCompleted && OrdersCompleted` |

All events are from `Haworks.Contracts.Privacy`:
- `PrivacyErasureRequested(Guid RequestId, Guid UserId)`
- `PrivacyErasureCompleted(Guid RequestId, Guid UserId, string ServiceName)`
- `PrivacyErasureFailed(Guid RequestId, Guid UserId, string ServiceName, string ErrorMessage)`
- `PrivacyDataExportRequested(Guid RequestId, Guid UserId)`
- `PrivacyDataExportCompleted(Guid RequestId, Guid UserId, string ServiceName, string? DataLink)`
- `PrivacyDataExportFailed(Guid RequestId, Guid UserId, string ServiceName, string ErrorMessage)`

## Saga State Machine

`PrivacyRequestStateMachine` (MassTransit `MassTransitStateMachine<PrivacyRequestState>`):

```
[Initial]
    -- RequestInitiated -->
        Publish PrivacyErasureRequested
        Transition to [Processing]

[Processing]
    -- ErasureCompleted (identity-svc) -->
        Set IdentityCompleted = true
        If IdentityCompleted && OrdersCompleted -> Transition to [Completed]

    -- ErasureCompleted (orders-svc) -->
        Set OrdersCompleted = true
        If IdentityCompleted && OrdersCompleted -> Transition to [Completed]
```

The saga correlates all events by `RequestId` (= `CorrelationId`). Saga state is stored in `PrivacyDbContext` via the EF Core saga repository with optimistic concurrency (`Version` column).

## Configuration

| Key | Description |
|---|---|
| `ConnectionStrings:privacy` | PostgreSQL connection string |
| `RabbitMq:Host` | RabbitMQ broker hostname |
| `RabbitMq:Username` | RabbitMQ username (default: `guest`) |
| `RabbitMq:Password` | RabbitMQ password (default: `guest`) |
| `Authentication:Authority` | JWKS endpoint for JWT validation |

EF Core migrations are applied automatically at startup (skipped in `Test` environment). Two migrations exist:
1. `20260514014811_InitialCreate` — core tables
2. `20260514015854_AddSagaAndOutbox` — saga state and outbox tables

## Database

- **Connection string key**: `privacy`
- **Schema**: default PostgreSQL public schema

### Tables

| Table | Description |
|---|---|
| `PrivacyRequests` | One row per data subject request |
| `PrivacyRequestSteps` | Per-service step completion tracking |
| `PrivacyRequestState` | MassTransit saga persistence; PK = `CorrelationId` |
| `OutboxMessage` | MassTransit transactional outbox messages |
| `OutboxState` | Outbox processor state |
| `InboxState` | Idempotent consumer inbox state |

### Indexes

- `PrivacyRequests (UserId)` — non-unique
- `PrivacyRequestSteps (RequestId)` — non-unique
- `PrivacyRequestState (CurrentState)` — max length 64; `Version` is a concurrency token

## Testing

| Project | Type | Location |
|---|---|---|
| `Privacy.Unit` | Unit tests (xUnit + FluentAssertions) | `tests/Privacy.Unit/` |
| `Privacy.Architecture` | Architecture tests (NetArchTest) | `tests/Privacy.Architecture/` |

**Unit tests** (`tests/Privacy.Unit/Domain/PrivacyRequestTests.cs`):
- Domain aggregate state transition tests for `PrivacyRequest`

No integration tests are present. Any additions must use `SharedTestPostgres.CreateDatabaseAsync("privacy")` from `BuildingBlocks.Testing.Containers`.
