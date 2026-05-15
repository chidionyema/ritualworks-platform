# BffWeb Service

Backend-for-Frontend that aggregates checkout initiation and delivers real-time saga progress to browser clients via SignalR.

## Responsibilities
- Accept checkout requests, compute SHA-256 idempotency key, forward to CheckoutOrchestrator
- Bridge MassTransit saga events to SignalR groups so the UI receives live step updates
- Consume CDC events from Debezium/Kafka for product and order data

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| POST | `/api/checkout` | Returns `{ sagaId, orderId }` |

## SignalR Hub
- Clients join group `saga-{sagaId}` to receive step events
- Bridge consumers: `StockReservedSagaBridge`, `PaymentSessionCreatedSagaBridge`, `PaymentCompletedSagaBridge`, `StockReservationFailedSagaBridge`, `CheckoutAbandonedSagaBridge`

## Events Consumed
- `StockReservedEvent`, `PaymentSessionCreatedEvent`, `PaymentCompletedEvent`
- `StockReservationFailedEvent`, `CheckoutAbandonedEvent`
- CDC topics: product / order change streams (Debezium → Kafka)

## Events Published
- `CheckoutInitiatedCommand` → CheckoutOrchestrator

## Infrastructure Dependencies
- RabbitMQ via MassTransit
- Kafka (CDC consumer)
- SignalR (in-process hub)

## Configuration
```
ConnectionStrings:rabbitmq
Kafka:BootstrapServers
CheckoutOrchestrator:BaseUrl
```

## Health Checks
- Default ASP.NET health endpoint via `MapDefaultEndpoints()`
