# Scheduler Service

Durable job scheduling via Hangfire backed by PostgreSQL. Publishes scheduled domain events to the bus.

## Responsibilities
- Store and execute scheduled jobs using Hangfire (PostgreSQL storage)
- Implement `IEventScheduler` via `HangfireEventScheduler` — enqueues/delays domain event publication
- Publish deferred events through MassTransit transactional outbox

## Infrastructure Dependencies
- PostgreSQL (`SchedulerDbContext`) with EF Core outbox
- Hangfire with PostgreSQL storage (`UsePostgreSqlStorage`)
- RabbitMQ via MassTransit (skipped in `Test` environment)

## Configuration
```
ConnectionStrings:scheduler
RabbitMq:Host / Username / Password
```

## Health Checks
- Hangfire dashboard at `/hangfire` (internal only)
- DB: `AddDbHealthCheck<SchedulerDbContext>()`
