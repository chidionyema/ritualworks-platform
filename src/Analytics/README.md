# Analytics Service

Stateless clickstream ingestion service. Accepts user interaction events via REST and flushes them to Kafka for downstream warehousing and reporting.

## Responsibilities
- Accept clickstream events via `POST /api/events`
- Buffer events in-memory via a channel-based pipeline
- Flush batches to Kafka via a background hosted service

## API Endpoints
| Method | Route | Auth | Response |
|--------|-------|------|----------|
| POST | `/api/events` | None | 202 Accepted |

## Domain Entities
- **ClickstreamEvent** — EventName, UserId, SessionId, OccurredAt, Metadata

## Events Published
- Clickstream events to Kafka topic (via `KafkaFlushingService`)

## Events Consumed
None.

## Infrastructure Dependencies
- Kafka (Confluent) — event sink
- No database — fully stateless

## Configuration
```
Kafka:BootstrapServers    Kafka broker addresses
```
