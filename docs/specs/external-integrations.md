# External Integration Specs

> Staff-level decision: use battle-tested OSS/SaaS instead of building from scratch.

## 1. A/B Testing — GrowthBook

### Why GrowthBook over custom

| Aspect | Our FeatureFlags | GrowthBook |
|--------|-----------------|------------|
| Percentage rollout | ✅ SHA-256 stable hash | ✅ Same approach + sticky bucketing |
| Statistical significance | ❌ Not implemented | ✅ Bayesian + frequentist engine |
| Experiment lifecycle | ❌ Manual | ✅ Start/stop/analyze with auto-duration |
| Metric tracking | ❌ None | ✅ Connects to any data source (Kafka, Postgres, Mixpanel) |
| Visual editor | ❌ API only | ✅ Web UI for non-engineers |
| Mutual exclusion | ❌ Not supported | ✅ Namespaces prevent experiment collision |
| Targeting | ✅ UserId + Region | ✅ Any attribute (plan, country, device, custom) |

### Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ GrowthBook (Docker container, self-hosted)                   │
│ http://growthbook:3100                                       │
│                                                             │
│ • Experiment definitions (UI or API)                         │
│ • Feature flag overrides                                     │
│ • Statistical analysis engine                                │
│ • Webhook on experiment state change                         │
└────────────────────┬────────────────────────────────────────┘
                     │ SDK API (JSON features endpoint)
                     ▼
┌─────────────────────────────────────────────────────────────┐
│ .NET Services (BFF, Catalog, Checkout)                       │
│                                                             │
│ • GrowthBook .NET SDK (NuGet: growthbook-c-sharp)            │
│ • Evaluates flags locally (no network call per request)      │
│ • Fires trackingCallback → Kafka clickstream                 │
└────────────────────┬────────────────────────────────────────┘
                     │ Experiment exposure events
                     ▼
┌─────────────────────────────────────────────────────────────┐
│ Kafka (existing clickstream pipeline)                        │
│                                                             │
│ • Topic: experiment-exposures                                │
│ • GrowthBook data source reads from Kafka/Postgres           │
│ • Computes: conversion rate, uplift, p-value, confidence     │
└─────────────────────────────────────────────────────────────┘
```

### Integration Points

| Component | Change |
|-----------|--------|
| `docker-compose.yml` | Add `growthbook` service (port 3100) |
| `BffWeb/Program.cs` | Register `IGrowthBook` singleton, configure SDK with API key |
| `Analytics` | Forward experiment exposures to `experiment-exposures` Kafka topic |
| `FeatureFlags` | Keep for simple on/off flags; delegate experiments to GrowthBook |
| Aspire AppHost | Add GrowthBook container |

### SDK Usage in .NET

```csharp
// DI registration
builder.Services.AddSingleton<GrowthBook>(sp => {
    var gb = new GrowthBook.GrowthBook(new GrowthBook.Context {
        ApiHost = "http://growthbook:3100",
        ClientKey = config["GrowthBook:ClientKey"],
        TrackingCallback = (experiment, result) => {
            // Fire to Kafka clickstream
            analyticsBuffer.Enqueue(new ExperimentExposureEvent {
                UserId = currentUser.UserId,
                ExperimentKey = experiment.Key,
                VariationId = result.VariationId,
            });
        }
    });
    return gb;
});

// Usage in handler
var showNewCheckout = growthBook.IsOn("new-checkout-flow");
var pricingVariant = growthBook.GetFeatureValue("pricing-display", "control");
```

### Deployment

```yaml
# docker-compose addition
growthbook:
  image: growthbook/growthbook:latest
  ports:
    - "3100:3100"   # API
    - "3200:3200"   # Admin UI (dev only)
  environment:
    - MONGODB_URI=mongodb://mongo:27017/growthbook
    - APP_ORIGIN=http://localhost:3100
    - API_HOST=http://localhost:3100
  volumes:
    - growthbook-data:/usr/local/share/growthbook
```

### Migration Plan

1. Deploy GrowthBook container alongside existing stack
2. Keep existing FeatureFlags for simple on/off flags (backward compatible)
3. New experiments use GrowthBook SDK exclusively
4. Migrate percentage rollouts to GrowthBook experiments over time
5. Eventually deprecate custom FeatureFlags rules that have GrowthBook equivalents

---

## 2. Shipping — EasyPost API

### Why EasyPost

| Aspect | Build custom | EasyPost |
|--------|-------------|----------|
| Multi-carrier | ❌ Each carrier = separate API | ✅ 100+ carriers, one API |
| Rate shopping | ❌ Build comparison logic | ✅ `GET /shipments/{id}/rates` |
| Label generation | ❌ Per-carrier format | ✅ Standardized PDF/ZPL/PNG |
| Tracking | ❌ Poll each carrier | ✅ Webhooks for all status changes |
| Address validation | ❌ Build or buy separately | ✅ Built-in verification |
| Returns | ❌ Reverse logistics is complex | ✅ `POST /shipments` with `is_return=true` |
| Insurance | ❌ Not feasible to build | ✅ Built-in or third-party |
| Pricing | N/A | Free tier: 120k labels/year, then $0.01-0.05/label |

### Architecture

```
┌──────────────┐     ┌──────────────────┐     ┌──────────────┐
│ Orders       │────▶│ Shipping Service  │────▶│ EasyPost API │
│ (OrderPaid)  │     │ (thin adapter)    │     │              │
└──────────────┘     │                   │     │ • Rates      │
                     │ • CreateShipment   │     │ • Labels     │
┌──────────────┐     │ • BuyLabel        │     │ • Tracking   │
│ BFF          │────▶│ • GetTracking     │     │ • Webhooks   │
│ (user views) │     │ • HandleWebhook   │     └──────────────┘
└──────────────┘     └────────┬─────────┘
                              │ Events (outbox)
                              ▼
                     ┌──────────────────┐
                     │ ShipmentCreated   │
                     │ ShipmentShipped   │
                     │ ShipmentDelivered │
                     │ ShipmentException │
                     └──────────────────┘
                              │
                     ┌────────▼─────────┐
                     │ Notifications    │ (tracking emails)
                     │ Orders           │ (status update)
                     │ Audit            │ (compliance)
                     └──────────────────┘
```

### Domain Model

```csharp
public class Shipment
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string EasyPostShipmentId { get; private set; }  // ep_xxxxx
    public ShipmentStatus Status { get; private set; }      // Created, LabelPurchased, InTransit, Delivered, Exception
    public string CarrierCode { get; private set; }         // usps, ups, fedex
    public string ServiceLevel { get; private set; }        // Priority, Ground, Express
    public string TrackingNumber { get; private set; }
    public string TrackingUrl { get; private set; }
    public string LabelUrl { get; private set; }
    public decimal RateAmount { get; private set; }
    public string RateCurrency { get; private set; }
    public Address FromAddress { get; private set; }
    public Address ToAddress { get; private set; }
    public DateTime? ShippedAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public DateTime? EstimatedDelivery { get; private set; }
}

public enum ShipmentStatus
{
    Created,           // Shipment created, rates fetched
    LabelPurchased,    // Label bought, awaiting carrier pickup
    InTransit,         // Carrier has the package
    OutForDelivery,    // Last-mile delivery
    Delivered,         // Confirmed delivered
    Exception,         // Delivery exception (returned, damaged, etc.)
    Cancelled
}
```

### API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | /api/shipments | Create shipment + fetch rates |
| POST | /api/shipments/{id}/buy | Buy cheapest/selected rate |
| GET | /api/shipments/{id} | Get shipment details + tracking |
| GET | /api/shipments/{id}/label | Get label PDF URL |
| GET | /api/shipments/by-order/{orderId} | Get shipments for an order |
| POST | /api/shipments/webhooks/easypost | EasyPost tracking webhook |

### Events

| Event | Trigger | Consumers |
|-------|---------|-----------|
| ShipmentCreatedEvent | Shipment created with rates | Notifications (confirmation) |
| ShipmentShippedEvent | Label purchased / carrier scan | Orders (status update), Notifications |
| ShipmentDeliveredEvent | Delivery confirmed | Orders, Notifications, Analytics |
| ShipmentExceptionEvent | Delivery exception | Notifications (alert), Support |

### EasyPost Client Integration

```csharp
// DI
builder.Services.AddSingleton(new EasyPost.Client(new EasyPost.ClientConfiguration(
    config["EasyPost:ApiKey"])));

// Create shipment
var shipment = await client.Shipment.Create(new() {
    FromAddress = new() { Street1 = "...", City = "...", State = "...", Zip = "...", Country = "US" },
    ToAddress = new() { Street1 = "...", City = "...", State = "...", Zip = "...", Country = "US" },
    Parcel = new() { Length = 10, Width = 8, Height = 4, Weight = 16 },
});

// Buy cheapest rate
var bought = await client.Shipment.Buy(shipment.Id, shipment.LowestRate());
// bought.TrackingCode, bought.PostageLabel.LabelUrl
```

### Webhook Handling

```csharp
// EasyPost sends tracking updates to /api/shipments/webhooks/easypost
// Verify signature, update Shipment entity, publish events
[HttpPost("webhooks/easypost")]
public async Task<IActionResult> HandleWebhook([FromBody] JsonElement payload)
{
    var eventType = payload.GetProperty("description").GetString();
    // "tracker.updated" → update status
    // "tracker.delivered" → mark delivered
}
```

---

## 3. FeatureFlags → Unleash (or keep + GrowthBook hybrid)

### Recommendation: Hybrid approach

Keep your custom FeatureFlags for **operational flags** (kill switches, maintenance mode, gradual rollouts). Use GrowthBook for **experiments** (A/B tests with metrics).

| Use Case | Tool |
|----------|------|
| Kill switch (`maintenance-mode`) | Your FeatureFlags (instant, cached) |
| Gradual rollout (`new-checkout-flow`) | GrowthBook (with experiment tracking) |
| Per-user override (`beta-tester-features`) | Your FeatureFlags (simple rules) |
| A/B test (`pricing-display-variant`) | GrowthBook (statistical analysis) |

If you want to consolidate to one tool: **Unleash** (OSS) supports both operational flags AND experiments. But it's a separate deployment + migration effort.

---

## 4. Notifications → Novu (when complexity grows)

### Current state is solid

Your multi-provider with Polly circuit breakers, template rendering, preference suppression, and delivery tracking is production-grade for the current scale.

### When to migrate to Novu

| Signal | Action |
|--------|--------|
| > 50 notification templates | Novu's visual editor saves engineering time |
| In-app notification feed needed | Novu has this built-in |
| Digest/batching required ("5 items shipped" instead of 5 emails) | Novu's digest engine |
| Non-engineers need to edit templates | Novu's no-code editor |
| Multi-tenant notification routing | Novu's tenant isolation |

### Novu Integration (when ready)

```yaml
# docker-compose addition
novu-api:
  image: ghcr.io/novuhq/novu/api:latest
  environment:
    - MONGO_URL=mongodb://mongo:27017/novu
    - REDIS_HOST=redis
novu-worker:
  image: ghcr.io/novuhq/novu/worker:latest
novu-web:
  image: ghcr.io/novuhq/novu/web:latest  # Admin UI
  ports:
    - "4200:4200"
```

Your existing `NotificationCreatedEvent` becomes a Novu trigger:
```csharp
await novu.Event.Trigger("order-confirmation", new TriggerEventRequest {
    To = new { subscriberId = userId, email = customerEmail },
    Payload = new { orderId, totalAmount, items }
});
```

---

## Integration Priority

| # | Integration | Effort | Value | Do Now? |
|---|------------|--------|-------|---------|
| 1 | GrowthBook (A/B testing) | S (2-3 days) | High — unlocks experimentation | ✅ Yes |
| 2 | EasyPost (Shipping) | M (1 week) | High — new service, fulfillment capability | ✅ Yes |
| 3 | Unleash (replace FeatureFlags) | M | Medium — migration cost vs benefit | ❌ Later |
| 4 | Novu (replace Notifications) | L | Low — current system is adequate | ❌ When signals hit |
