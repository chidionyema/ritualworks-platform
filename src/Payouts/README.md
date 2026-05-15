# Payouts Service

Marketplace seller payouts via Stripe Connect, triggered by completed payments.

## Responsibilities
- Consume `PaymentCompletedEvent` and initiate payouts to sellers via `StripePayoutGateway`
- Manage seller `SellerProfile` (KYC status, commission percentage, payout schedule)
- Schedule payout reconciliation jobs via Hangfire

## Domain Entities
- **SellerProfile** — `SellerId`, `ExternalProviderId` (Stripe Connect account), `KycStatus`, `PayoutsEnabled`, `PayoutSchedule`, `CommissionPercentage`

## Events Consumed
- `PaymentCompletedEvent` (`PaymentCompletedConsumer`)

## Events Published
- `PayoutInitiatedEvent`
- `PayoutFailedEvent`

## Infrastructure Dependencies
- PostgreSQL (`PayoutsDbContext`) with EF Core outbox
- RabbitMQ via MassTransit (skipped in `Test` environment)
- Hangfire with PostgreSQL storage (`UsePostgreSqlStorage`)
- Stripe Connect SDK (`StripePayoutGateway`)

## Configuration
```
ConnectionStrings:payouts
RabbitMq:Host / Username / Password
Stripe:SecretKey
```

## Health Checks
- DB: `AddDbHealthCheck<PayoutsDbContext>()`
