# Pricing Service

## Overview

The Pricing service owns the **pricing rules, discounts, and promotions** bounded context. It is intended to provide dynamic price calculation for catalog items, supporting features such as tiered pricing, discount codes, promotional rules, and margin-based pricing adjustments.

> **Current Status**: This service is a structural scaffold. The four-layer project structure (`Pricing.Domain`, `Pricing.Application`, `Pricing.Infrastructure`, `Pricing.Api`) is present and compiles, but no source business logic has been implemented yet. All layers contain only the generated build artifacts. The test projects (`Pricing.Unit`, `Pricing.Integration`) are also empty scaffolds.

## Architecture

The service is designed to follow the same Clean Architecture pattern used across the platform:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Pricing.Domain` | Pricing rules, discount aggregates, value objects (not yet implemented) |
| Application | `Pricing.Application` | MediatR commands/queries for price calculation and rule management (not yet implemented) |
| Infrastructure | `Pricing.Infrastructure` | EF Core persistence, rule storage, MassTransit wiring (not yet implemented) |
| API | `Pricing.Api` | ASP.NET Core controllers, JWT auth (not yet implemented) |

Based on the platform-wide patterns used in sibling services, the following dependencies are expected:
- **MediatR** — CQRS dispatch
- **FluentValidation** — command validation via `ValidationBehavior<,>` pipeline
- **MassTransit 8.x + RabbitMQ** — event publishing with transactional outbox
- **EF Core 9 + Npgsql** — PostgreSQL persistence, schema-per-service
- **Haworks.BuildingBlocks.Authentication** — JWKS-based JWT validation
- **Serilog** — structured logging

## Domain Model

No domain entities, value objects, or aggregates have been implemented.

Anticipated domain concepts based on the bounded context:
- **PricingRule** — a rule associating a product or category with a base price or margin
- **Discount** — a reusable discount definition (percentage, fixed amount, or free shipping)
- **Promotion** — a time-bounded promotional campaign applying one or more discounts
- **PriceQuote** — a value object representing the calculated price for a given input context

## API Endpoints

No endpoints have been implemented.

## Events

No events are published or consumed.

## Configuration

No configuration keys have been established. When implemented, the service will require at minimum:

| Key | Description |
|---|---|
| `ConnectionStrings:pricing` | PostgreSQL connection string |
| `RabbitMq:Host` | RabbitMQ broker hostname |
| `RabbitMq:Username` | RabbitMQ username |
| `RabbitMq:Password` | RabbitMQ password |
| `Authentication:Authority` | JWKS endpoint for JWT validation |

## Database

- **Connection string key**: `pricing` (anticipated)
- **Schema**: default PostgreSQL public schema (anticipated)

No tables or migrations exist.

## Testing

| Project | Type | Location |
|---|---|---|
| `Pricing.Unit` | Unit tests (xUnit + FluentAssertions) | `tests/Pricing.Unit/` |
| `Pricing.Integration` | Integration tests (WebApplicationFactory) | `tests/Pricing.Integration/` |

Both projects are empty scaffolds with no test cases.

When integration tests are added, they must use `SharedTestPostgres.CreateDatabaseAsync("pricing")` from `BuildingBlocks.Testing.Containers`. Raw `PostgreSqlBuilder` or `ContainerBuilder` usage is prohibited per the platform CI architecture check (`scripts/check-architecture.sh`).
