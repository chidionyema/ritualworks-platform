# Realtime Service

SignalR-based real-time notification delivery with Redis backplane for multi-instance scale-out. Consumes domain events and pushes notifications to connected clients.

## Responsibilities
- Maintain persistent SignalR connections with authenticated clients
- Consume domain events (e.g., `OrderStatusChanged`) and push to relevant users
- Buffer notifications in Redis inbox for offline users, flush on reconnect
- Scale horizontally via Redis SignalR backplane

## Architecture
```
RabbitMQ → MassTransit Consumer → SendNotificationCommand → SignalR Hub → Client
                                                         ↘ Redis Inbox (if offline)
```

## SignalR Hub
- **NotificationHub** (`[Authorize]`)
  - Client method: `ReceiveNotification`
  - On connect: flushes pending messages from Redis inbox
  - User identified via JWT `sub` claim

## Events Consumed
- `OrderStatusChanged` → triggers `SendNotificationCommand`

## Events Published
None.

## Infrastructure Dependencies
- Redis — SignalR backplane + notification inbox (`RedisInboxService`)
- RabbitMQ via MassTransit (consumer transport)
- No database

## Configuration
```
Redis:ConnectionString            Redis connection string
RabbitMq:Host / Username / Password
```

## Health Checks
- Redis readiness check
