# Location Service

Geospatial address management with PostGIS. Stores addresses as WGS-84 points, supports nearby search, and geocodes via Nominatim.

## Responsibilities
- Persist addresses with PostGIS `Point` geometry (SRID 4326) and 12-char geohash
- Geocode free-text addresses via Nominatim API
- Execute PostGIS spatial proximity queries for nearby-address lookup

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| POST | `/api/addresses` | Geocode + persist |
| GET | `/api/addresses/nearby` | `?lat=&lon=&radiusKm=` |

## Domain Entities
- **Address** — `Street`, `City`, `Postcode`, `Country`, `Coordinates` (PostGIS `Point`, SRID 4326), `Geohash`, `Metadata` (JSON)

## Events Published
- `AddressCreatedEvent`

## Infrastructure Dependencies
- PostgreSQL + PostGIS (`LocationDbContext`) with NetTopologySuite
- Nominatim HTTP geocoding API
- RabbitMQ via MassTransit (transactional outbox)

## Configuration
```
ConnectionStrings:location
Nominatim:BaseUrl
RabbitMq:Host / Username / Password
```

## Health Checks
- DB: `AddDbHealthCheck<LocationDbContext>()`
