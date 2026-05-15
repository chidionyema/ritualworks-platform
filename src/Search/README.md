# Search Service

Full-text product search powered by Elasticsearch with saved-search percolation and CDC-driven index updates.

## Responsibilities
- Index product documents into Elasticsearch via CDC events (Debezium → Kafka)
- Out-of-order suppression using `SourceVersion` field
- Fetch fresh data from Catalog on cache invalidation, upsert to Elasticsearch
- Run percolation after index updates to notify saved-search subscribers
- Expose search query and saved-search management API

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| GET | `/search` | `?q=&categoryId=&page=&pageSize=` |
| POST | `/search/saved` | Create saved search (percolation query) |

## Events Consumed
- `ProductCacheInvalidatedEvent` (`ProductCacheInvalidatedConsumer`) via Kafka CDC topic

## Infrastructure Dependencies
- Elasticsearch (index + percolation)
- Kafka (CDC consumer — topic: `db.catalog.public.products`)
- PostgreSQL (`SearchDbContext`) for saved searches
- HTTP client → Catalog service

## Configuration
```
ConnectionStrings:search
Elasticsearch:Uri / Username / Password
Kafka:BootstrapServers / GroupId
Catalog:BaseUrl
```

## Health Checks
- Elasticsearch connectivity
- DB: `AddDbHealthCheck<SearchDbContext>()`
