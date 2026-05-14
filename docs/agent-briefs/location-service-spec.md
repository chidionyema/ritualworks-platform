# Postcode & Location Service — End-to-End Spec (v2)

**Status:** Draft (2026-05-12) — Engine: Elasticsearch 8.x+, Source: PostgreSQL + PostGIS
**Implementer:** Gemini CLI agents working brief-by-brief
**Reviewer:** Principal Architect / User
**Target:** A robust Geospatial Master Data Management (MDM) hub for address identity and geo-location search.

---

## 1. Goal & non-goals

### Goal
A centralized "Source of Truth" for address identity and coordinate metadata, optimized for high-consistency storage and low-latency geospatial search. It manages both the "What" (Address Strings) and the "Where" (Coordinates/Polygons).

- **Identity Management:** Master address records with PostGIS accuracy.
- **Geospatial Search:** High-performance radius and polygon searches via Elasticsearch.
- **Decoupled Hydration:** gRPC-based flow to keep search indexes lean while providing rich data at the edge.
- **Transactional Integrity:** Event-driven updates via Outbox pattern.

### Non-goals
- **Routing/Navigation:** Not a turn-by-turn navigation engine (use OSRM/Google Maps for this).
- **Map Tile Serving:** Not a vector tile server (use Mapbox/Maptiler).
- **Global Geo-coding:** Not a replacement for Google Places API for fuzzy string-to-coord lookups of *unknown* addresses; this service manages *our* managed locations.

---

## 2. Architecture at a glance

The system follows a **Tiered Projection** strategy: optimize for Consistency at the source and Search Latency at the edge.

```
                                    ┌───────────────────────────┐
   user → BFF /location → flycast → │  location-svc (Fly, HA)   │
                                    │  ─────────────────────    │
                                    │  • PostGIS (PostgreSQL)   │
                                    │  • MediatR + Validators   │
                                    │  • Outbox + MassTransit   │
                                    └────────────┬──────────────┘
                                                 │ 1. Transactional Write
                                                 │ 2. Outbox Event
                                                 ▼
                                    ┌───────────────────────────┐
                                    │     RabbitMQ / Outbox     │
                                    └────────────┬──────────────┘
                                                 │ LocationUpdatedEvent
                                                 ▼
                                    ┌───────────────────────────┐
                                    │  search-svc / Consumer    │
                                    │  ─────────────────────    │
                                    │  • Elasticsearch 8.x Index│
                                    │  • Redis Tier 0 Cache     │
                                    └────────────▲──────────────┘
                                                 │
                                                 │ 3. Geo-Search
                                                 │
   Client Search ────────────────────────────────┘
```

---

## 3. Contracts

### 3.1 Message Contract (`Haworks.Contracts.Location`)

This contract ensures the Search Service can evolve its internal index without tight coupling to the Postcode Service's DB schema.

```csharp
namespace Haworks.Contracts.Location;

public record LocationUpdated(
    Guid LocationId,
    AddressInfo Address,
    double Latitude,
    double Longitude,
    string Geohash, // 12-char precision for grid-based pre-filtering
    Dictionary<string, string> Metadata // e.g., "Region": "Greater London"
);

public record AddressInfo(
    string Street, 
    string City, 
    string Postcode, 
    string Country
);
```

### 3.2 gRPC Hydration API

To keep the Search Index lean, we return IDs from search and "hydrate" full data via gRPC.

```protobuf
service LocationHydration {
  rpc GetAddresses(AddressRequest) returns (AddressList);
}

message AddressRequest {
  repeated string locationIds = 1;
}

message AddressList {
  repeated AddressDetail locations = 1;
}
```

---

## 4. Data Model

### 4.1 Tier 1: System of Record (PostgreSQL + PostGIS)

Stores the absolute truth. Uses `GEOGRAPHY(POINT, 4326)` for geodetic accuracy.

```sql
CREATE TABLE addresses (
    id UUID PRIMARY KEY,
    street TEXT NOT NULL,
    city TEXT NOT NULL,
    postcode TEXT NOT NULL,
    country TEXT NOT NULL,
    coordinates GEOGRAPHY(POINT, 4326) NOT NULL,
    geohash VARCHAR(12) NOT NULL,
    metadata JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_addresses_coords ON addresses USING GIST (coordinates);
CREATE INDEX idx_addresses_geohash ON addresses (geohash);
```

### 4.2 Tier 2: Search Projection (Elasticsearch 8.x)

Optimized for BKD-tree based geo-sorting and filtering.

**Mapping Spec:**
```json
{
  "mappings": {
    "properties": {
      "locationId": { "type": "keyword" },
      "location":   { "type": "geo_point" },
      "boundary":   { "type": "geo_shape" }, 
      "postcode":   { "type": "keyword" },
      "metadata":   { "type": "flattened" }
    }
  }
}
```

---

## 5. Implementation Logic

### 5.1 Atomic Event Flow (MassTransit .NET 9)
Uses the EF Core Outbox to ensure that `LocationUpdated` events are only dispatched if the Postgres transaction commits.

### 5.2 Geo-Search Optimization
- **Radius Search:** `geo_distance` query for precise circles.
- **Polygon Search:** `geo_shape` for "Point-in-Polygon" checks (e.g., Delivery Zones).
- **Distance Decay:** Use `gauss` decay functions in `function_score` to rank results by proximity to the user.

### 5.3 Performance Tiering
- **Tier 0 (Redis):** For high-density lookups (e.g., "London Center"), use `Redis GEOSEARCH` to avoid Elasticsearch overhead (<1ms).
- **Geohash Precision:** Store Geohashes at Level 7 (~150m) for fast cluster-based map visualizations.

---

## 6. SLA targets

| Metric                                  | Target            | How measured |
| --------------------------------------- | ----------------- | ------------ |
| Geo-Query p50 (Elasticsearch)           | < 15 ms           | trace span   |
| Geo-Query p99                           | < 50 ms           | trace span   |
| Hydration p99 (gRPC)                    | < 10 ms           | gRPC span    |
| Index Lag (Write → Searchable)          | < 2 s             | outbox delay |
| Availability                            | 99.99%            | HA across AZ |

---

## 7. Topology & deployment

- **PostgreSQL/PostGIS:** High-availability cluster with PostGIS extension enabled.
- **Elasticsearch:** 3-node cluster minimum for sharding and replicas.
- **Service:** Multi-instance deployment via Fly.io across regions.

---

## 8. Test Plan

### 8.1 Unit (`tests/Location.Unit/`)
- `AddressValidatorTests` — Ensure strict postcode and coordinate formatting.
- `GeohashServiceTests` — Verify precision levels and encoding logic.

### 8.2 Integration (`tests/Location.Integration/`)
- `PostGIS_SpatialQueryTests` — Verify radius checks in SQL.
- `Elasticsearch_GeoSearchTests` — Verify radius and polygon filters using Testcontainers.
- `Outbox_IntegrityTests` — Verify event dispatch on DB commit.

### 8.3 Performance (`tests/Location.Perf/`)
- Benchmark `Redis vs Elasticsearch` for hot postcode clusters.

---

## 9. Implementation Phases

1.  **Phase 1: Foundation.** Scaffold `Location.Api` with PostGIS and EF Core Outbox.
2.  **Phase 2: Eventing.** Implement `LocationUpdated` contract and MassTransit producer.
3.  **Phase 3: Search.** Implement `search-svc` consumer for Elasticsearch indexing.
4.  **Phase 4: Hydration.** Implement gRPC service for lean data retrieval.
5.  **Phase 5: Optimization.** Add Redis Tier 0 caching and Geohash visualization support.
