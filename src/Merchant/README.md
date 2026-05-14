# Merchant Service

## Overview

The Merchant service owns the **seller onboarding and profile** bounded context. It is responsible for creating and managing merchant profiles on the platform — the identity that sellers use to list products, set availability, and be discoverable by buyers. A merchant profile is linked to a platform user (via `OwnerId`) but is a distinct aggregate with its own lifecycle.

## Architecture

The service follows Clean Architecture with four layers:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Merchant.Domain` | Aggregates, enums, domain logic |
| Application | `Merchant.Application` | MediatR commands/queries, FluentValidation, pipeline behaviors |
| Infrastructure | `Merchant.Infrastructure` | EF Core persistence, MassTransit/RabbitMQ wiring, outbox |
| API | `Merchant.Api` | ASP.NET Core controllers, Swagger, JWT auth, migration runner |

Key dependencies:
- **MediatR** — CQRS dispatch
- **FluentValidation** — command validation via `ValidationBehavior<,>` pipeline
- **MassTransit 8.x + RabbitMQ** — message bus with transactional outbox (`AddEntityFrameworkOutbox`)
- **EF Core 9 + Npgsql** — PostgreSQL persistence
- **Haworks.BuildingBlocks.Authentication** — JWKS-based JWT validation (`AddJwksAuthentication`)
- **Serilog** — structured logging

## Domain Model

### Aggregates / Entities

**`MerchantProfile`** (aggregate root, extends `AuditableEntity`)

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `OwnerId` | `Guid` | Reference to the platform user who owns this merchant |
| `Name` | `string` | Display name (max 200 chars) |
| `Slug` | `string` | URL-safe unique identifier (max 100 chars, `^[a-z0-9-]+$`) |
| `Bio` | `string?` | Optional description |
| `Status` | `MerchantStatus` | Current lifecycle state |

Domain methods: `Activate()`, `Suspend()`

**`OperatingHours`** (entity, extends `AuditableEntity`)

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `MerchantId` | `Guid` | FK to `MerchantProfile` |
| `DayOfWeek` | `int` | 0 (Sunday) to 6 (Saturday) |
| `OpenTime` | `TimeSpan` | Opening time |
| `CloseTime` | `TimeSpan` | Closing time |

### Enums

**`MerchantStatus`**: `Active`, `Suspended`, `Maintenance`

## API Endpoints

Base path: `/api/merchants`

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/merchants` | JWT required | Create a new merchant profile |

**POST `/api/merchants` — request body:**

```json
{
  "ownerId": "guid",
  "name": "string",
  "slug": "string"
}
```

**Response:**
```json
{ "merchantId": "guid" }
```

Validation rules (enforced by FluentValidation):
- `OwnerId`: required, non-empty GUID
- `Name`: required, max 200 characters
- `Slug`: required, max 100 characters, must match `^[a-z0-9-]+$`, must be unique

## Events

### Published

None currently. The outbox infrastructure is configured and ready; no domain events are published by the `CreateMerchant` handler at this time.

### Consumed

None. The service does not subscribe to any external events.

## Configuration

| Key | Description |
|---|---|
| `ConnectionStrings:merchant` | PostgreSQL connection string for the merchant database |
| `RabbitMq:Host` | RabbitMQ broker hostname |
| `RabbitMq:Username` | RabbitMQ username (default: `guest`) |
| `RabbitMq:Password` | RabbitMQ password (default: `guest`) |
| `Authentication:Authority` | JWKS endpoint for JWT validation |

EF Core migrations are applied automatically at startup (skipped in `Test` environment).

## Database

- **Connection string key**: `merchant`
- **Schema**: default PostgreSQL public schema

### Tables

| Table | Description |
|---|---|
| `MerchantProfile` | Merchant profiles (one per seller) |
| `OperatingHours` | Day-of-week operating hours per merchant |
| `OutboxMessage` | MassTransit transactional outbox messages |
| `OutboxState` | Outbox processor state |
| `InboxState` | Idempotent consumer inbox state |

### Indexes

- `MerchantProfile.Slug` — unique index
- `MerchantProfile.OwnerId` — non-unique index for owner lookups
- `OperatingHours.MerchantId` — non-unique index

## Testing

| Project | Type | Location |
|---|---|---|
| `Merchant.Unit` | Unit tests (xUnit + FluentAssertions) | `tests/Merchant.Unit/` |
| `Merchant.Architecture` | Architecture tests (NetArchTest) | `tests/Merchant.Architecture/` |

**Unit tests** (`tests/Merchant.Unit/Domain/MerchantProfileTests.cs`):
- `Create_Should_Set_Initial_Status_To_Active` — verifies initial status and OwnerId assignment
- `Suspend_Should_Change_Status_To_Suspended` — verifies state transition

No integration tests are present. The service does not use raw Testcontainers; any integration test additions must use `SharedTestPostgres.CreateDatabaseAsync("merchant")` from `BuildingBlocks.Testing.Containers`.
