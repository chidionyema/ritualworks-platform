# Pricing Service

Manages pricing rules, discounts, and price calculation for catalog products.

## Responsibilities
- Store and manage pricing rules per product / category
- Calculate effective price applying discounts, promotions, and tiered rules
- Expose pricing query API consumed by Catalog and BffWeb

## Infrastructure Dependencies
- PostgreSQL (`PricingDbContext`)
- RabbitMQ via MassTransit (transactional outbox)

## Configuration
```
ConnectionStrings:pricing
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<PricingDbContext>()`
