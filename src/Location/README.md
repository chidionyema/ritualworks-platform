# Location Service

## Overview

The Location service is the bounded context responsible for address management and geospatial queries. It stores address records with geodetic coordinates (WGS 84 / SRID 4326) in PostGIS, generates high-precision geohashes for grid-based pre-filtering, and exposes proximity search via PostGIS `ST_Distance` queries.

The service also exposes a gRPC endpoint (`LocationHydrationService`) that allows other services (notably Search) to resolve full address details for a list of location IDs in a single call, avoiding N+1 lookups.

---

## Architecture

### Layers

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Location.Domain` | `Address` entity with PostGIS `Point` coordinates |
| Application | `Location.Application` | `CreateAddressCommand` + handler, geocoding/geohash interfaces, FluentValidation |
| Infrastructure | `Location.Infrastructure` | `LocationDbContext` (EF Core + NetTopologySuite), `NominatimGeocodingService`, `GeohashService`, MassTransit outbox, Vault integration |
| API | `Location.Api` | REST controller, gRPC `LocationHydrationService`, Swagger |

### Key Dependencies

- **EF Core 9 + Npgsql.NetTopologySuite** — PostGIS geometry storage and spatial queries; `UseNetTopologySuite()` enabled on the Npgsql provider
- **NetTopologySuite** — `Point` type for `SRID 4326` geodetic coordinates; `ST_Distance` used for proximity filtering
- **MassTransit 8.x + RabbitMQ** — transactional outbox for `LocationUpdated` domain events
- **gRPC** — `LocationHydrationService` for bulk address hydration by ID list
- **Nominatim (OpenStreetMap)** — external HTTP geocoding API (`https://nominatim.openstreetmap.org/`); `NominatimGeocodingService` typed HttpClient with `User-Agent: RitualworksPlatform/1.0`
- **Vault** — dynamic Postgres credentials (role: `haworks-location`; disabled in `Test`)
- **Serilog** — structured logging

---

## Domain Model

### Address

The only entity in this bounded context.

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key (from `AuditableEntity`) |
| `Street` | `string` | Street address line |
| `City` | `string` | City name |
| `Postcode` | `string` | Postal code |
| `Country` | `string` | Country |
| `Coordinates` | `Point` | PostGIS geometry; SRID 4326 (WGS 84); stored as `GEOGRAPHY` for metre-accurate distance queries |
| `Geohash` | `string` | 12-character geohash for grid-based spatial pre-filtering |
| `Metadata` | `string?` | Arbitrary JSON for region, district, or business tags |

`Address` extends `AuditableEntity` (`CreatedAt`, `LastModifiedDate`).

### CreateAddressCommand (Application)

MediatR command that drives address creation. Coordinates are optional; if omitted, the handler geocodes the address via Nominatim (falling back to postcode-only geocoding if full-address geocoding fails). After geocoding, a 12-character geohash is generated. The `LocationUpdated` domain event is published via the MassTransit outbox.

```csharp
public record CreateAddressCommand : IRequest<Guid>
{
    public required string Street    { get; init; }
    public required string City      { get; init; }
    public required string Postcode  { get; init; }
    public required string Country   { get; init; }
    public double? Latitude          { get; init; }  // optional; geocoded if omitted
    public double? Longitude         { get; init; }  // optional; geocoded if omitted
}
```

---

## API Endpoints

### REST

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/addresses` | None | Create an address. Accepts `CreateAddressCommand` JSON body. Geocodes if coordinates are absent. Returns the new address `Guid`. |
| `GET` | `/api/addresses/nearby` | None | Proximity search using PostGIS `ST_Distance`. Query params: `lat` (double), `lon` (double), `radiusMeters` (double, default 5 000). Returns ordered list of `{id, street, postcode, distance}`. |

### gRPC

| Service | Method | Description |
|---|---|---|
| `LocationHydration` | `GetAddresses(AddressRequest)` | Accepts a list of location ID strings; returns full `AddressDetail` records (`id`, `street`, `city`, `postcode`, `country`, `latitude`, `longitude`). Used by Search for result hydration. |

The gRPC service is mapped at startup via `app.MapGrpcService<LocationHydrationService>()`.

---

## Events

### Published (via MassTransit transactional outbox)

| Event | When |
|---|---|
| `LocationUpdated` | After a new `Address` is persisted. Contains `LocationId`, `AddressInfo` (street, city, postcode, country), `Latitude`, `Longitude`, `Geohash`. |

### Consumed

This service does not consume domain events from other services.

---

## Configuration

| Key | Source | Description |
|---|---|---|
| `ConnectionStrings:location` | Aspire `WithReference(locationDb)` | PostGIS-enabled PostgreSQL connection string (required) |
| `ConnectionStrings:rabbitmq` | Aspire `WithReference(rabbitmq)` | RabbitMQ AMQP URI (required in non-Test environments) |
| `Vault:Enabled` | Environment variable | Enables Vault dynamic Postgres credentials (disabled in `Test`) |
| `MigrateDatabase` | Environment variable / app config | Forces EF Core migration in the `Test` environment when set to `true` (default: `false`) |

The Nominatim geocoder base address is hardcoded to `https://nominatim.openstreetmap.org/` and is not configurable at runtime. The `User-Agent` header is set to `RitualworksPlatform/1.0` to comply with Nominatim's usage policy.

---

## Database

- **Schema**: `location`
- **Migration history table**: `location.__EFMigrationsHistory`
- **DbContext**: `LocationDbContext`
- **Auto-migrate**: on startup (skipped in `Test` unless `MigrateDatabase=true`)
- **PostGIS requirement**: the database must have the PostGIS extension installed. Use `SharedTestPostGIS.CreateDatabaseAsync("location")` in tests (not `SharedTestPostgres`).

### Key Tables

| Table | Description |
|---|---|
| `location.addresses` | Address records with PostGIS `geography` column and geohash |
| `location.outbox_messages` | MassTransit EF Core outbox for `LocationUpdated` events |
| `location.outbox_state` | MassTransit outbox delivery state |
| `location.inbox_state` | MassTransit inbox deduplication |

### Spatial Index

EF Core migrations add a GIST spatial index on `addresses.coordinates` to support efficient `ST_Distance` / `ST_DWithin` queries.

---

## Testing

Test projects live under `tests/Location/`.

| Project | Type | Coverage |
|---|---|---|
| `Location.Unit` | Unit | `Commands/` — handler logic unit tests; `Validators/` — FluentValidation tests |
| `Location.Integration` | Integration | Full address creation and proximity search flows with shared PostGIS container; uses `SharedTestPostGIS.CreateDatabaseAsync("location")` |

### Integration Test Files

| File | What it tests |
|---|---|
| `LocationFlowsTests.cs` | Address creation (with and without coordinates), geocoding fallback path, `GET /nearby` proximity search, `LocationUpdated` event publication via outbox, gRPC `GetAddresses` hydration |

Integration tests use `LocationWebAppFactory` with the `Test` environment, which suppresses Vault and Kafka. The geocoding service is replaced with a test double that returns deterministic coordinates to avoid network calls to Nominatim.
