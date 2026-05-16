# Cross-Cutting Services — Roadmap

Spec coverage for microservices that aren't yet implemented but are
likely needed before this platform can ship to real customers. Pairs
with the existing `*-service-spec.md` files (search, payments, audit,
webhooks) and the in-flight Notifications service.

This file is the index. Tier 1 services have full specs in sibling
files; Tier 2 entries are scoped here so they're ready to expand into
their own spec when the time comes; Tier 3 is parked.

**Reusability discipline:** every cross-cutting service must pass the
architecture check at `scripts/check-architecture.sh` (no cross-service
project references; no domain-specific Contracts imports in cross-cutting
services). Coupling inventory + decoupling moves per existing service in
[`../architecture/cross-cutting-coupling-audit.md`](../architecture/cross-cutting-coupling-audit.md).

---

## Tier 1 — almost universal (most projects need them)

| Service              | What it owns                            | Why cross-cutting                                                                                          |
|----------------------|-----------------------------------------|------------------------------------------------------------------------------------------------------------|
| `identity-svc`       | Users, JWKS, sessions                   | Already in stack. Bar is high for reuse — most platforms have idiosyncratic auth.                          |
| `audit-svc`          | Append-only event log                   | Already in stack. THE most reusable cross-cutting svc when designed event-shape-agnostic. Has coupling debt — see audit doc. |
| `notifications-svc`  | Email/SMS/push delivery                 | Already in stack. Reusable when recipients are abstract (`recipient_id` + channel-prefs lookup) not domain-typed. |
| `content-svc`        | Blob storage, virus scan, signed URLs   | Already in stack. Highly reusable — files are universal.                                                   |
| `search-svc`         | Full-text search over indexed docs      | Already in stack. Reusable IF the index schema is dynamic (jsonb) not Product-specific. Has coupling debt. |
| `payments-svc`       | Payment intents, providers, refunds     | Already in stack. Reusable when "what's being paid for" is `purchase_id` + amount, not Order.              |
| `webhooks-svc`       | Outbound webhook delivery + retries     | Spec'd in `webhooks-service-spec.md`. Universal. **Build trigger:** first external integration partner.    |
| `scheduler-svc`      | Cron + delayed jobs (run X at Y)        | Generic background work, not in stack yet. Build when first feature needs durable scheduling.              |
| `feature-flags-svc`  | Feature-flag evaluation, A/B targeting  | Universal; LaunchDarkly-like, low-feature variant suffices. Build when first gradual rollout is needed.    |
| `rate-limit-svc`     | Token-bucket rate limiting              | Cross-cutting; sometimes a sidecar (e.g. envoy) instead of a service. Build when API abuse becomes real.   |

## Tier 2 — common but domain-shaped

| Service              | What it owns                          | Caveat                                                                              |
|----------------------|---------------------------------------|-------------------------------------------------------------------------------------|
| `pricing-svc`        | Promotions, quote engine              | Spec'd in `pricing-service-spec.md`. Reusable when "what's priced" is line-item-shaped, not Product. |
| `tax-svc`            | Tax calculation per jurisdiction      | Roadmap'd § below. Reusable across any commerce.                                    |
| `inventory-svc`      | Multi-warehouse stock                 | Roadmap'd § below. Reusable for physical goods only.                                |
| `shipping-svc`       | Carrier integrations                  | Roadmap'd § below. Physical goods only.                                             |
| `comments-svc`       | Threaded comments on `entity_id`      | Universal across UGC platforms.                                                     |
| `moderation-svc`     | Manual + ML content moderation        | Wraps an external API typically.                                                    |
| `analytics-svc`      | Event ingestion → warehouse           | Internal-tool typically; very reusable.                                             |
| `i18n-svc`           | Translation strings, locale routing   | Generic.                                                                            |
| `doc-gen-svc`        | PDF / invoice / receipt generation    | Wraps a templating engine + headless browser.                                       |
| `image-pipeline-svc` | Thumbnails, transcoding, transforms   | Wraps libvips / ffmpeg.                                                             |

---

## Current service inventory

| Service               | Status        | Owns                                                  |
| --------------------- | ------------- | ----------------------------------------------------- |
| Identity              | done          | auth, JWT, roles, vault rotation                      |
| Catalog               | done          | products, categories, stock reservation               |
| Orders                | done          | order lifecycle                                       |
| Payments              | done          | payment sessions, refunds, subscriptions              |
| CheckoutOrchestrator  | done          | saga between Catalog/Orders/Payments                  |
| Content               | done          | uploads, presigned URLs, virus scan                   |
| Search                | done          | Elasticsearch indexing + query                        |
| BffWeb                | done          | gateway/BFF                                           |
| Notifications         | done (L0–L4)  | in-app + email + push templated delivery              |

29+ published event contracts across `src/Contracts/` — all listener-ready.

---

## Tier 1 — usually cross-cutting, almost always needed

### 1. Audit / Activity log
**Status:** spec'd in `audit-service-spec.md`. Pure listener (no new
contracts), 1–2 day port. Highest leverage for compliance + support
investigations. Build first. Per-phase Gemini briefs at
`docs/agent-briefs/audit/{L0-skeleton, L1A-extractors-redactor,
L1B-capture-pipeline, L1C-query-api, L1D-export-partition-cron}.md`.

### 2. Webhooks / Integrations
**Status:** spec'd in `webhooks-service-spec.md`. Outbound delivery to
merchants/partners. Build when you have the first external integrator
asking for it; until then the spec is enough.

### 3. Email/SMS gateway
**Status:** **collapsed into Notifications.** The Notifications service
in `src/Notifications/` ships with channel dispatchers under
`Notifications.Infrastructure/Channels/{Email,Push,InApp}/`. The "thin
SES/Postmark wrapper" pattern lives there as the Email channel. No
separate gateway service needed unless a non-templated transactional
email sender is required (rare — bank-style "your password was
changed" can still be a Notifications template).

If you ever need to split it out: candidate name `comms-gateway-svc`,
extracts `Channels/Email/*` + `Channels/Sms/*` and turns them into a
process that Notifications calls over RabbitMQ. Don't pre-build.

---

## Tier 2 — domain-specific but cross-domain

These have enough scope to warrant their own service when the platform
needs the feature. Each is sketched to ~30 lines so it can be expanded
into a full `*-service-spec.md` without re-discovering the design.

### 4. Pricing / Promotions / Coupons (`pricing-svc`)

**Why a separate service:** Catalog owns list price; Orders / Checkout
owns final total. Promo logic — codes, %-off, BOGO, bundles, dynamic /
time-limited prices, customer-segment pricing — belongs neither in
Catalog (which would bloat the product aggregate) nor in Checkout (which
would bury rules under saga code). Extracting it lets marketing/ops
ship promos without redeploying the product or order services.

**Scope:**
- CRUD for promotions (codes, eligibility rules, validity windows, stacking).
- `POST /price/quote` — inputs: cart lines + customer + locale; output: itemized discount + final price.
- Subscribes to `OrderCreated` to record redemption + retire single-use codes.
- Publishes `PromotionApplied` (cart_id, code, discount_amount), `CouponRedeemed` (code, order_id), `PromotionRetired` (code, reason).

**Data model:** `promotions`, `promotion_rules`, `promotion_redemptions`, `customer_segments`. Postgres.

**Cross-service touchpoints:**
- BffWeb / Checkout calls `POST /price/quote` during cart total recompute.
- Catalog provides product → category mapping to `pricing-svc` via Catalog API (no event coupling needed).
- Subscribes to `OrderCreated`; doesn't block order flow (eventual record).

**Build trigger:** when the product team ships its first `% off` promo. Until then a stub coupon table inside Orders is fine.

### 5. Inventory (`inventory-svc`)

**Why a separate service:** Catalog already exposes `StockReservationRequested/Reserved/Released` events. That's a thin reservation layer over a single `quantity` column. Real inventory needs:
- Multi-warehouse stock-on-hand
- Restock receiving (PO + GRN)
- Cycle counts / shrinkage adjustments
- Backorder rules
- Reservation TTLs that don't hold against `quantity` directly

**Scope:**
- Owns `stock_levels` (sku × warehouse), `reservations` (with TTL), `inventory_movements` (audit of every +/-).
- HTTP: `POST /reserve`, `POST /release`, `POST /adjust`, `GET /availability`.
- Subscribes to `OrderCreated` (firm reservation), `OrderCompleted` (decrement on-hand), `OrderCancelled` (release).
- Publishes `LowStockAlert`, `RestockReceived`, `StockReservedFirm`, `StockReleased`.

**Migration from Catalog:** Catalog keeps SKU master + product details; inventory stops being a `Quantity` column on Product and moves to `inventory-svc`. Catalog's existing `StockReservation*Event` contracts are renamed and republished from `inventory-svc`. CheckoutOrchestrator's saga points at the new service.

**Build trigger:** first time you need either a second warehouse, backorders, or honest-to-god shrinkage tracking.

### 6. Tax (`tax-svc`)

**Why a separate service:** Tax is regulator-driven and changes outside the product calendar. Avalara / TaxJar / Stripe Tax are the right answer 95% of the time; a thin wrapper centralizes the integration so neither Orders nor Checkout has to import the SDK twice with possibly-different versions.

**Scope:**
- `POST /tax/calculate` — inputs: line items, ship-to address, customer tax exemption status; output: line-item tax + total + jurisdiction breakdown.
- `POST /tax/commit` — finalizes a tax record on a confirmed order (some providers need this for nexus reporting).
- `POST /tax/refund` — issues a refund record on the provider side when Payments emits `RefundIssued`.
- No domain events published; pure RPC.

**Data model:** thin — `tax_calculations` (idempotency cache), `tax_commits` (audit). Most state lives in the upstream provider.

**Cross-service touchpoints:**
- Checkout calls `POST /tax/calculate` after `pricing-svc` returns discounted prices.
- Orders calls `POST /tax/commit` on `OrderCompleted`.
- Subscribes to `RefundIssued` to call `POST /tax/refund`.

**Build trigger:** first sale across a state line where you collect tax. EU VAT counts. UK VAT counts. Until then, a hard-coded percentage on Orders is fine and legally fine for hobbyist sales.

### 7. Shipping / Fulfillment (`shipping-svc`)

**Why a separate service:** Shipping integrates with carrier APIs (USPS, UPS, FedEx, EasyPost, ShipStation) for rate quotes, label generation, and tracking webhooks. Each integration is messy auth + XML/JSON dialects + retry quirks. Centralizing means Orders doesn't grow a `usps_account_id` column.

**Scope:**
- `POST /shipping/rate-quote` — inputs: ship-to, package dims/weight; output: list of (carrier, service, price, eta).
- `POST /shipping/label` — inputs: order_id + chosen rate; output: label PDF URL + tracking number. Side-effect: charges the carrier.
- Subscribes to `OrderCompleted` (auto-generate label if customer pre-selected service).
- Receives carrier tracking webhooks (`POST /webhooks/{carrier}/tracking`); publishes `OrderShipped`, `OrderInTransit`, `OrderDelivered`, `TrackingUpdated`.

**Data model:** `shipments` (order_id, carrier, tracking_number, label_url), `tracking_events` (timestamped), `carrier_accounts` (credentials, refresh).

**Cross-service touchpoints:**
- Checkout calls `POST /shipping/rate-quote` during cart finalize.
- Orders subscribes to `OrderShipped` / `OrderDelivered` to update its own status.
- Notifications subscribes to `OrderShipped` / `OrderDelivered` for emails.

**Build trigger:** first physical-good order that needs to actually leave a warehouse. Digital-only platforms skip forever.

---

## Tier 3 — defer until needed

Each is a real service when the time comes, but carries enough
complexity that you should NOT pre-build. Stub in the closest existing
service or wait until product asks.

| Service           | Stub it where                                       | Build trigger                                         |
| ----------------- | --------------------------------------------------- | ----------------------------------------------------- |
| Reviews/Ratings   | Catalog (1:N table on product)                      | when moderation, helpful-vote, or photo reviews land  |
| Wishlist          | BffWeb session + Identity user metadata             | when wishlist sharing / "back in stock" emails ship   |
| Loyalty/Rewards   | Identity (points balance column)                    | when first tiered program launches                    |
| Support/Tickets   | external SaaS (Zendesk/Front)                       | rarely worth building in-house                        |
| Reports/Analytics | external warehouse (BigQuery, Snowflake, Metabase)  | rarely worth building in-house                        |
| Feature flags     | external SaaS (LaunchDarkly, GrowthBook, Unleash)   | almost never worth building in-house                  |

The "external SaaS" rows aren't pessimism — they're the right answer.
A Reviews service inside this platform is a real one-week project; a
ticketing service is a six-month one with no platform-level
differentiation.

---

## Build order — recommended

When the team has bandwidth:

1. **Audit log** — pure listener, 1–2 days, immediate compliance + support payoff. See `audit-service-spec.md` and the per-phase briefs under `docs/agent-briefs/audit/`.
2. **Webhooks** — when first external partner asks. Spec'd in `webhooks-service-spec.md`.
3. **Pricing** — when first promo is requested.
4. **Tax** — when first cross-jurisdiction sale (or earlier if VAT-mandated jurisdiction).
5. **Inventory** — when multi-warehouse OR backorder OR cycle count enters scope.
6. **Shipping** — when first physical fulfillment.
7. Tier 3 — only when a product manager files a real ticket.

---

## What this roadmap explicitly does NOT cover

- Per-service rewrites of existing services (Catalog, Identity, etc.) — those are out of scope.
- Operational/runbook docs — those live in `docs/runbooks/`.
- Observability stack (Tempo + Grafana) — covered by the observability work landed in PRs #13–#15.
