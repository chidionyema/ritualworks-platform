# Payouts Service

## Overview

The Payouts service owns the **seller disbursement and ledger** bounded context. It is responsible for:

- Registering sellers with a payment provider (Stripe Connect)
- Maintaining a double-entry ledger that tracks funds across account types (platform holding, seller pending, seller payable, external transit)
- Maturing pending seller funds into payable balances on a scheduled basis
- Disbursing payable balances to sellers via Stripe Transfers

The service is the financial core for seller monetization. It receives `PaymentCompletedEvent` from the Payments service, credits the relevant seller ledger, and uses Hangfire recurring jobs to mature funds and execute disbursements.

## Architecture

The service follows Clean Architecture with four layers:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Payouts.Domain` | Aggregates, enums, domain logic |
| Application | `Payouts.Application` | MediatR commands/queries, application services (`LedgerService`, `DisbursementService`) |
| Infrastructure | `Payouts.Infrastructure` | EF Core persistence, Stripe gateway, MassTransit consumer, Hangfire wiring |
| API | `Payouts.Api` | ASP.NET Core controllers, Hangfire dashboard, recurring job registration, migration runner |

Key dependencies:
- **MediatR** — CQRS dispatch
- **FluentValidation** — command validation via pipeline behavior
- **MassTransit 8.x + RabbitMQ** — consumes `PaymentCompletedEvent` on queue `payouts-payment-completed`
- **Hangfire + Hangfire.PostgreSql** — recurring job scheduling (daily disbursements, hourly fund maturity)
- **Stripe.net** — Stripe Connect Express accounts and Stripe Transfers
- **EF Core 9 + Npgsql** — PostgreSQL persistence
- **Haworks.BuildingBlocks.Authentication** — JWKS-based JWT validation
- **Serilog** — structured logging

## Domain Model

### Aggregates / Entities

**`SellerProfile`** (extends `AuditableEntity`)

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `SellerId` | `Guid` | Reference to the platform user |
| `ExternalProviderId` | `string?` | Stripe Connect account ID (e.g. `acct_xxx`) |
| `KycStatus` | `string?` | KYC verification status (default: `"Pending"`) |
| `PayoutsEnabled` | `bool` | Whether payouts are active for this seller |
| `PayoutSchedule` | `string` | Cadence: `Monthly`, `Weekly`, `Daily`, `Threshold` |
| `PayoutThreshold` | `decimal` | Minimum balance before disbursement (default: 50.00) |
| `CommissionPercentage` | `decimal` | Platform commission rate (default: 10.00%) |

**`LedgerAccount`** (extends `AuditableEntity`)

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `OwnerId` | `Guid` | Platform system GUID or seller GUID |
| `Type` | `AccountType` | Account classification |
| `Currency` | `string` | ISO 4217 currency code (e.g. `USD`) |
| `Balance` | `decimal` | Current balance |

Domain methods: `UpdateBalance(amount, entryType)`

**`LedgerEntry`** (extends `AuditableEntity`)

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `AccountId` | `Guid` | FK to `LedgerAccount` |
| `TransactionId` | `Guid` | Groups debit/credit pair of a single transaction |
| `Amount` | `decimal` | Entry amount |
| `Type` | `EntryType` | `Credit` or `Debit` |
| `Description` | `string` | Human-readable description |
| `ReferenceId` | `string` | External reference (order ID, payout ID, etc.) |

**`Payout`** (extends `AuditableEntity`)

| Property | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `SellerId` | `Guid` | Seller being paid |
| `Amount` | `decimal` | Amount of the disbursement |
| `Currency` | `string` | Currency code |
| `Status` | `PayoutStatus` | Current state |
| `ExternalReference` | `string?` | Stripe Transfer ID |
| `FailureReason` | `string?` | Error message if failed |
| `ScheduledFor` | `DateTimeOffset?` | When the payout was scheduled |
| `ProcessedAt` | `DateTimeOffset?` | When the payout was completed or failed |

Domain methods: `MarkInTransit(externalRef)`, `MarkSucceeded()`, `MarkFailed(reason)`

### Enums

**`AccountType`**: `PlatformHolding`, `PlatformRevenue`, `SellerPending`, `SellerPayable`, `ExternalTransit`

**`EntryType`**: `Credit`, `Debit`

**`PayoutStatus`**: `Pending`, `Scheduled`, `InTransit`, `Succeeded`, `Failed`, `Cancelled`

### Ledger Flow

```
PaymentCompletedEvent received
  -> LedgerService.CreditSellerAsync
       -> Credit SellerPending account
       -> Debit PlatformHolding account

MatureFundsCommand (hourly Hangfire job)
  -> For each SellerPending account with Balance > 0
       -> Debit SellerPending
       -> Credit SellerPayable

DisbursementService.ProcessEligiblePayoutsAsync (daily Hangfire job)
  -> For each SellerPayable account where Balance >= PayoutThreshold and PayoutsEnabled
       -> Create Payout record
       -> Call IPayoutGateway.InitiatePayoutAsync (Stripe Transfer)
       -> MarkSucceeded or MarkFailed
       -> Debit SellerPayable
```

## API Endpoints

All endpoints require a valid JWT.

### Sellers

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/sellers` | JWT required | Register a seller with Stripe Connect |
| `POST` | `/api/sellers/{sellerId}/onboarding-link` | JWT required | Generate a Stripe Connect onboarding URL |

**POST `/api/sellers` — request body:**
```json
{ "sellerId": "guid", "email": "string" }
```
Response: `{ "profileId": "guid" }`

**POST `/api/sellers/{sellerId}/onboarding-link` — query params:** `returnUrl`, `refreshUrl`
Response: `{ "url": "https://connect.stripe.com/..." }`

### Payouts

| Method | Route | Auth | Description |
|---|---|---|---|
| `GET` | `/api/payouts/seller/{sellerId}` | JWT required | List all payouts for a seller (newest first) |

### Ledger

| Method | Route | Auth | Description |
|---|---|---|---|
| `GET` | `/api/ledger/balance/{ownerId}` | JWT required | Get ledger balance for an owner |

**GET `/api/ledger/balance/{ownerId}` — query params:** `type` (`AccountType` enum value), `currency` (default: `USD`)
Response: `{ "balance": decimal, "currency": "USD" }`

## Events

### Published

None directly. The outbox infrastructure is configured but no domain events are published from handlers at this time.

### Consumed

| Event | Queue | Source | Action |
|---|---|---|---|
| `PaymentCompletedEvent` | `payouts-payment-completed` | Payments service | Credits seller's `SellerPending` ledger account |

`PaymentCompletedEvent` fields used: `OrderId`, `PaymentId`, `Amount`, `Currency`.

> Note: Seller resolution from `OrderId` is currently a placeholder (`Guid.NewGuid()`); production implementation requires a lookup from the Orders service.

## Configuration

| Key | Description |
|---|---|
| `ConnectionStrings:payouts` | PostgreSQL connection string |
| `RabbitMq:Host` | RabbitMQ broker hostname |
| `RabbitMq:Username` | RabbitMQ username (default: `guest`) |
| `RabbitMq:Password` | RabbitMQ password (default: `guest`) |
| `Stripe:SecretKey` | Stripe secret API key (`sk_live_...` or `sk_test_...`) |
| `Authentication:Authority` | JWKS endpoint for JWT validation |

EF Core migrations are applied automatically at startup (skipped in `Test` environment).

## Database

- **Connection string key**: `payouts`
- **Schema**: default PostgreSQL public schema

### Tables

| Table | Description |
|---|---|
| `LedgerAccounts` | One row per (OwnerId, AccountType, Currency) — unique composite index |
| `LedgerEntries` | Immutable double-entry ledger lines |
| `Payouts` | Disbursement records per seller |
| `SellerProfiles` | Seller payout configuration and Stripe Connect linkage |

### Indexes

- `LedgerAccounts (OwnerId, Type, Currency)` — unique
- `LedgerEntries (AccountId)` — non-unique
- `LedgerEntries (TransactionId)` — non-unique (groups debit/credit pairs)
- `Payouts (SellerId)` — non-unique
- `SellerProfiles (SellerId)` — unique

## Testing

| Project | Type | Location |
|---|---|---|
| `Payouts.Unit` | Unit tests (xUnit + FluentAssertions) | `tests/Payouts.Unit/` |
| `Payouts.Integration` | Integration tests (WebApplicationFactory) | `tests/Payouts.Integration/` |
| `Payouts.Architecture` | Architecture tests (NetArchTest) | `tests/Payouts.Architecture/` |

**Integration test setup** (`PayoutsWebAppFactory`):
- Uses `SharedTestPostgres.CreateDatabaseAsync("payouts")` from `BuildingBlocks.Testing.Containers`
- Mocks `IPayoutGateway` (returns `("tr_test", PayoutStatus.Succeeded)`)
- Uses `MassTransitTestHarness` to replace the real RabbitMQ bus
- Uses `TestAuthenticationHandler` to bypass JWKS JWT validation

The Stripe gateway is replaced with a mock in all tests; no real Stripe calls are made.
