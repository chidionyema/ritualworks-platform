# Location Service — Architectural Overview

**Implemented:** May 2026
**Engine:** Elasticsearch 8.x + PostgreSQL/PostGIS
**Role:** Geospatial Master Data Management (MDM) hub.

## 1. Core Architecture

The Location Service follows a **Tiered Projection Strategy**, prioritizing geodetic accuracy at the source (PostgreSQL) and sub-millisecond search latency at the edge (Elasticsearch).

### Tier 1: Authority (PostgreSQL + PostGIS)
- **Schema:** `location.Addresses` table.
- **Accuracy:** Uses `GEOGRAPHY(POINT, 4326)` for geodetic precision (earth curvature aware).
- **Persistence:** Coordinates are stored alongside a **Level 12 Geohash** (approx. 3.7cm precision).
- **Integrity:** Uses the **Transactional Outbox Pattern** via MassTransit EF Core integration. Every address change is atomically committed with a `LocationUpdated` integration event.

### Tier 2: Search Index (Elasticsearch 8.x)
- **Projection:** The `search-svc` consumes `LocationUpdated` events to update a geospatial index.
- **Optimization:** Uses `geo_point` for radius/polygon filtering and `flattened` for metadata.
- **Hydration Pattern:** Search results return only IDs; the BFF "hydrates" full address strings via gRPC calls back to the Location Service. This keeps the Elasticsearch RAM-resident BKD trees lean and fast.

## 2. Geospatial Logic

### Geocoding
- **Provider:** OpenStreetMap Nominatim API.
- **Workflow:** Missing coordinates in a `CreateAddressCommand` trigger an automated geocoding lookup. Fallback to postcode-only geocoding is implemented for maximum resilience.

### Geohashing
- **Precision:** 12-character geohashes are generated for every location.
- **Utility:** Enables grid-based pre-filtering and dynamic map clustering without the overhead of complex polygon math in the hot path.

## 3. Communication Patterns

### Messaging (MassTransit)
- **Event:** `Haworks.Contracts.Location.LocationUpdated`.
- **Consumer:** `Haworks.Search.Application.Consumers.LocationUpdatedConsumer` in the `search-svc`.
- **Integrity:** EF Core Outbox ensures zero message loss during database updates.

### gRPC Hydration
- **Service:** `LocationHydrationService`.
- **Endpoint:** `GetAddresses(AddressRequest)`.
- **Performance:** Sub-10ms response times via binary Protobuf serialization.

## 4. Testing Strategy

### Unit Tests
- `tests/Location/Location.Unit/`
- 100% coverage of address validation and geohash encoding logic.
- Mocked geocoding and publisher interfaces to ensure deterministic CI runs.

### Integration Tests
- `tests/Location/Location.Integration/`
- Uses **Testcontainers** to spin up a real **PostGIS** instance (`postgis/postgis:16-3.4-alpine`).
- Verifies real SQL spatial queries and EF migrations.

### E2E Tests
- `tests/E2E/LocationE2ETests.cs`
- Verified via **Playwright** through the **BFF-Web** proxy.
- Validates the entire flow from client HTTP POST to PostgreSQL persistence.
