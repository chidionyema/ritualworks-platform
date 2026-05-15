# Wave 3: Cross-Cutting Platform Services Specification

This document details the architectural specifications for the Wave 3 expansion of the RitualWorks platform. These 6 new cross-cutting microservices transform the architecture from a robust e-commerce/transactional system into a true plug-and-play platform ecosystem capable of supporting arbitrary business domains globally at massive scale.

---

## 1. Feature Flags & Configuration Service (`FeatureFlags`)

**Purpose:** Decouple deployment from release. Centralized management of feature toggles, A/B testing configurations, and dynamic limits.

### Architecture
*   **Storage:** PostgreSQL (Source of Truth, Audit History) + Redis (High-performance evaluation).
*   **Evaluation Model:** Rules are evaluated *at the edge* (in the BFF or Frontend) or locally within microservices to prevent network latency. 
*   **Sync Mechanism:** The service publishes `FeatureFlagUpdated` events via MassTransit. Subscribing microservices keep an in-memory dictionary (e.g., using `MemoryCache`) of the current ruleset.

### Edge Cases & Resilience
*   **Redis/DB Outage (Fail-Open):** If the service is down, client SDKs/microservices MUST fall back to a hardcoded local `default_ruleset.json` to prevent catastrophic platform failure.
*   **Stale Caches:** If a microservice misses a MassTransit update, it might serve an old flag. Mitigation: Polling fallback every 60 seconds if no events are received.
*   **Targeting Complexity:** Support for percentage-based rollouts (e.g., 10% of users), targeting by `UserId` (via Identity JWT claims), or geographic regions (via Location headers).

---

## 2. Localization & Internationalization Service (`Localization`)

**Purpose:** Centralized repository for translation strings, currency formatting, and regional compliance configurations.

### Architecture
*   **Storage:** PostgreSQL using `JSONB` columns for fast querying of nested translation trees.
*   **Delivery:** Translations are compiled into immutable language packs (e.g., `en-US.json`, `fr-FR.json`) and pushed directly to an edge CDN (Cloudflare/CloudFront).
*   **Integration:** The `BffWeb` layer intercepts incoming requests, reads the `Accept-Language` header, and either proxies the CDN file to the frontend or hydrates dynamic backend responses before returning them.

### Edge Cases & Resilience
*   **Missing Keys:** Strict fallback hierarchy: `fr-CA` -> `fr-FR` -> `en-US`. If a key is missing entirely, return the raw key name (e.g., `[error.unknown_user]`) and fire an async `TranslationMissingEvent` to alert the content team.
*   **Cache Invalidation:** Updating a typo must not require a deployment. The service will use CDN cache invalidation tags to purge old language packs globally within seconds.
*   **Pluralization & Formatting:** Strings must support ICU Message Format to handle complex pluralization (e.g., "1 item", "2 items", "0 items") and RTL (Right-to-Left) languages seamlessly.

---

## 3. Media & Asset Management Service (`Media`)
*(Adapted and evolved from the current `Content` service)*

**Purpose:** Generic wrapper over object storage handling file uploads, sanitization, resizing, and CDN distribution.

### Architecture
*   **Storage:** PostgreSQL (Metadata: Hash, Size, MIME type, UploaderId) + AWS S3 (Raw Bytes).
*   **Upload Pattern:** Direct-to-S3 Pre-signed URLs. The client requests an upload URL from the `Media` service, then uploads the file directly to S3. This completely bypasses the API, saving bandwidth and IOPS.
*   **Processing:** S3 triggers an AWS Lambda (or async MassTransit consumer) to run ClamAV virus scanning and generate thumbnails (WebP/AVIF).

### Edge Cases & Resilience
*   **Malicious Files:** Files are initially uploaded to a `quarantine-bucket`. Only after passing the virus scan does the service move them to the `public-bucket` and set the DB status to `Active`.
*   **Orphaned Uploads:** Clients might request a pre-signed URL but never upload. A nightly `BackgroundService` sweeps the DB for `Pending` uploads older than 24 hours and deletes the records.
*   **Deduplication:** Files are hashed (SHA-256) on the client side. If the hash already exists in the DB, the service immediately returns the existing CDN URL, saving storage costs.

---

## 4. Real-time Messaging & Presence Service (`Realtime`)

**Purpose:** Manages persistent bidirectional communication (WebSockets) to offload connection-management overhead from business APIs.

### Architecture
*   **Tech Stack:** ASP.NET Core SignalR with a Redis Backplane.
*   **Integration:** Completely decoupled. If the `Orders` service completes an order, it publishes an `OrderStatusChanged` event to MassTransit. The `Realtime` service consumes this, looks up the active SignalR connection for the `UserId`, and pushes the payload to the frontend.

### Edge Cases & Resilience
*   **Horizontal Scaling & Backplane:** WebSockets are stateful. If a user connects to Instance A, and the MassTransit consumer runs on Instance B, the message must be routed correctly. The Redis Backplane handles this cross-instance broadcasting automatically.
*   **Connection Drops (The "Lost Message" Gap):** Mobile devices drop connections frequently. If a message arrives while the user is disconnected, it is saved to a short-lived Redis List (Inbox). Upon reconnection, the client requests `SyncMissedMessages`.
*   **Massive Fan-out:** Global announcements (e.g., "Platform maintenance in 5 mins") broadcasting to 100,000+ active connections can cause thread starvation. Broadcasts must be batched and rate-limited.

---

## 5. Analytics & Telemetry Ingestion Service (`Analytics`)

**Purpose:** High-throughput, low-latency ingestion endpoint for frontend tracking events (clickstreams, funnels).

### Architecture
*   **Tech Stack:** Extremely lightweight ASP.NET Core API writing directly to Apache Kafka or Amazon Kinesis.
*   **Data Lake:** Kafka sinks the data into a Data Warehouse (Snowflake, BigQuery, or ClickHouse) for BI querying.
*   **Constraint:** This service MUST NOT use PostgreSQL. Relational databases will buckle under high-volume clickstream inserts.

### Edge Cases & Resilience
*   **Surge Traffic:** Must accept payloads in memory and acknowledge (`HTTP 202 Accepted`) immediately. Batching occurs in the background before flushing to Kafka.
*   **Bot Traffic & Spam:** Implements aggressive IP rate limiting and drops invalid payloads. Requires schema validation at the edge (using FluentValidation) to prevent bad data from corrupting the Data Warehouse.
*   **Late-Arriving Events:** Mobile clients might go offline and flush a batch of events hours later. The service must trust the client-provided `OccurredAt` timestamp, not the server's `ReceivedAt` timestamp, for accurate funnel analysis.

---

## 6. Dynamic Rules & Pricing Engine (`RulesEngine`)

**Purpose:** Allows product managers to define complex logical rules dynamically via a UI, removing hardcoded `if/else` logic from core services.

### Architecture
*   **Evaluation Core:** Parses JSON-based ASTs (Abstract Syntax Trees) into compiled C# expression trees at runtime using a library like `Microsoft.RulesEngine` or a custom evaluator.
*   **Integration:** 
    *   *Option A (Centralized):* Services like `CheckoutOrchestrator` call the `RulesEngine` via gRPC for synchronous evaluation (e.g., "Calculate Cart Total").
    *   *Option B (Distributed Edge):* For ultra-low latency, the `RulesEngine` compiles the ruleset and distributes it to microservices via MassTransit. Services evaluate the rules locally in-memory.

### Edge Cases & Resilience
*   **Infinite Loops & Performance:** Badly written rules can cause stack overflows. The evaluator must have a strict timeout (e.g., 50ms max execution time) and limit recursion depth.
*   **Auditability & Traceability:** When a customer asks "Why was I charged this much?", support teams need an answer. Every rule evaluation must output a `TraceLog` detailing exactly which rule ID fired and what the inputs were, saved to the `Audit` service.
*   **Rule Conflicts:** If Rule A says "10% off" and Rule B says "20% off", the engine needs conflict resolution strategies (e.g., Priority weights, or "Apply Best Discount").
