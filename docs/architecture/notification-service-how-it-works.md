# Notification Service — How It Works (As-Built)

A walk-through of the live service: code paths, data flow, deployment.
Companion to [`notification-service.md`](./notification-service.md) (the
design spec) and
[`docs/agent-briefs/notifications/`](../agent-briefs/notifications/)
(the implementation briefs).

> **Status:** the foundation (L0–L4) and follow-ups (F1–F5) all merged
> to main. Service builds clean, ~150 unit tests + 5 integration tests
> pass. Production-deployable via `git push`.

---

## 1. What it does

One API for every transactional/system message the platform sends.
Calling services hand off **what** to send (template + recipient +
variables) and the service handles **how**: channel selection, provider
failover, retries, suppression, audit trail.

```
Calling service                      Notification Service
  POST /api/notifications  ───────►  validate + idempotency check
  (or publish DomainEvent)           preference + suppression gate
                                     write Notification row + outbox event
                                     ↓
                                     NotificationDispatchConsumer
                                     ↓
                                     channel gateway (Email/Sms/Push)
                                     ↓
                                     provider 1 → if Retryable, fall through
                                     provider 2 → ...
                                     ↓
                                     status: Sent (with provider message id)
                                     ↓
                                     provider's webhook callback (later)
                                     ↓
                                     status: Delivered / Bounced / Complained
```

---

## 2. The 4 projects

```
src/Notifications/
├── Notifications.Domain/         pure entities + value objects + enums
├── Notifications.Application/    handlers + interfaces + consumers
├── Notifications.Infrastructure/ EF + MassTransit + provider impls
└── Notifications.Api/            controllers + Program.cs

tests/
├── Notifications.Unit/           ~150 tests (domain + handlers + providers + gateways)
└── Notifications.Integration/    5 tests (Testcontainers Postgres + WAF + mocks)
```

Same Clean Architecture layout as Identity / Orders / Payments. No
project ever depends inward (Domain has zero refs).

---

## 3. The 3 channels and 4 providers

| Channel | Implementations | Notes |
|---|---|---|
| **Email** | AWS SES (`Channels/Email/Ses/`), SendGrid (`Channels/Email/SendGrid/`) | Both registered in DI; `EmailChannelGateway` iterates them in registration order with per-provider Polly circuit breaker |
| **SMS** | Twilio (`Channels/Sms/Twilio/`) | Single provider today; gateway architecture supports adding more |
| **Push** | FCM (`Channels/Push/Fcm/`) | Mobile + web push via Firebase Admin SDK |

Each provider implements `IEmailProvider` / `ISmsProvider` /
`IPushProvider` (declared in
`src/Notifications/Notifications.Application/Channels/ProviderInterfaces.cs`)
returning `ProviderSendResult.{Success, Retryable, NonRetryable}`.

The 3 channel gateways
(`EmailChannelGateway`, `SmsChannelGateway`, `PushChannelGateway`) all
implement the same failover pattern (in
`src/Notifications/Notifications.Infrastructure/Channels/`):

```csharp
foreach (var provider in _providers)
{
    if (circuit_breaker_open) continue;
    var result = await provider.SendAsync(...);
    if (result.IsSuccess) { MarkSent(); return; }
    if (!result.IsRetryable) { MarkFailed(); return; }   // 4xx — same input fails everywhere
    // Retryable — fall through to next provider
}
notification.MarkFailed("all-providers-exhausted");
```

Per-provider Polly circuit-breaker state lives in a static
`ConcurrentDictionary` on each gateway, so breaker state survives
across the gateway's Scoped instances within the same process.

---

## 4. The pipeline (caller → recipient)

### Stage 1 — REST or event ingress

**HTTP path** — `POST /api/notifications` →
`NotificationsController` → `SendNotificationCommandHandler`
(`src/Notifications/Notifications.Application/Commands/SendNotificationCommand.cs`).
Inside the handler:
1. Generate idempotency key (SHA-256 over
   `userId + templateId + recipient + callerSuppliedKey`).
2. **Idempotency check** — if a Notification already exists with this
   key, return its `Id`.
3. **Preferences gate** (`IPreferencesService`) — global unsubscribe?
   per-category opt-out? quiet hours in user's timezone?
   frequency cap exceeded?
4. **Suppression gate** (`ISuppressionService`) — recipient on the
   hard-suppression list (hashed)? bounce/complaint sticky forever.
5. If gated: write the Notification row with terminal status
   `Suppressed` or `Failed` (not Created), return its Id.
6. **Otherwise**: `Notification.Create(...)` (status `Created`).
7. **Publish `NotificationCreatedEvent` BEFORE `SaveChanges`** —
   outbox semantics; the event row commits in the same EF transaction
   as the Notification row.

**Event path** — calling services in other bounded contexts (Orders,
Identity) publish their own domain events; future consumers in
`Notifications.Application/Consumers/` translate them into
`SendNotificationCommand`s. The `bff-web/hub` template entry already
exists in the Vault KV layout for the BFF's SignalR push integration.

### Stage 2 — Dispatch consumer

`NotificationDispatchConsumer`
(`src/Notifications/Notifications.Application/Consumers/NotificationRequestConsumer.cs`)
subscribes to `NotificationCreatedEvent`:

1. Load the `Notification` + matching `NotificationTemplate` from DB.
2. `notification.MarkRendering()` → `MarkQueued()` via the domain
   methods (state-transition guards in
   `Notifications.Domain.Entities.Notification`).
3. Render template via `ITemplateRenderer` (Scriban + MJML for HTML
   email, plain text fallback).
4. Dispatch via the right channel gateway based on `Notification.Channel`:
   - `Email` → `IEmailChannelGateway`
   - `Sms` → `ISmsChannelGateway`
   - `Push` → `IPushChannelGateway`
5. The gateway's failover loop handles provider attempts. Each attempt
   gets a `DeliveryAttempt` value-object recorded on the Notification.
6. On success: `notification.MarkSent(providerMessageId)`.
   On all-providers-fail: `notification.MarkFailed(reason)`.
7. `SaveChangesAsync` commits final state.

### Stage 3 — Provider does the actual send

The provider (e.g. `SesEmailProvider`) calls the vendor SDK
(`IAmazonSimpleEmailServiceV2.SendEmailAsync`), maps the response:

| Provider response | `ProviderSendResult` | Why |
|---|---|---|
| 2xx | `Success(messageId)` | Vendor accepted, will deliver |
| 429 / throttle | `Retryable(...)` | Try next provider or wait + retry |
| 5xx | `Retryable(...)` | Vendor temporarily unavailable |
| 4xx (validation, suppression, MailFromUnverified, AccountSuspended) | `NonRetryable(...)` | Same recipient/payload would fail elsewhere |

### Stage 4 — Inbound provider webhooks (delivery callbacks)

After the provider physically delivers (or bounces, or the recipient
clicks unsubscribe), it POSTs to one of:

- `POST /api/notifications/webhooks/ses` (SES via SNS)
- `POST /api/notifications/webhooks/sendgrid` (SendGrid Event Webhook)
- `POST /api/notifications/webhooks/twilio` (Twilio status callback)

Each controller:
1. **Verify signature** (SNS, SendGrid HMAC, Twilio HMAC) BEFORE any
   DB lookup — drops malicious replays at the edge.
2. **Idempotency** via `(provider, providerEventId)` dedup — replays
   return 200 OK without re-processing.
3. **Find Notification by `ProviderMessageId`** (set in Stage 3).
4. Map provider event → status transition:
   - `delivery` → `notification.MarkDelivered()`
   - `bounce` (hard) → `MarkBounced(reason)` + add recipient hash to
     `Suppression` table
   - `complaint` → `MarkComplained()` + add to suppression
   - `open` → `notification.MarkOpened()` (email/push only)

---

## 5. Data model (Postgres `notifications` schema)

| Table | Purpose |
|---|---|
| `notifications` | Aggregate root. One row per send attempt with status, idempotency key (UNIQUE), recipient hash, variables JSONB, scheduled_at, timestamps. |
| `delivery_attempts` | One row per provider call against a notification (provider name, attempted_at, success/error, provider_message_id). |
| `notification_templates` | Versioned + locale-keyed templates. `(template_id, version, locale)` PK; `is_active` flag flips atomically. |
| `notification_preferences` | Per-user. `global_unsubscribed`, `quiet_hours_*` (with IANA tz), `by_category` JSONB (channel × category × {enabled, daily_cap}). |
| `suppression` | Hard list. `(recipient_hash, channel)` PK; reasons: `hard_bounce` / `complaint` / `user_unsubscribe` / `manual`. Durable forever by default. |
| `rate_limit_buckets` | Sliding-window counters per `(user_id, category, hour)`. Reaped >24h. |

EF Core schema lives in
`src/Notifications/Notifications.Infrastructure/Persistence/NotificationsDbContext.cs`.
Initial migration ran via `EnsureCreatedAsync` in tests; production
uses standard `MigrateAsync` on startup.

---

## 6. Idempotency

```
idempotencyKey = SHA-256(
  tenantId + ":" +
  templateId + ":" +
  canonicalRecipient + ":" +    // email lowercased / phone E.164
  callerSuppliedKey)
```

Caller MUST supply `callerSuppliedKey` (HTTP `X-Idempotency-Key`
header for direct API; for event-driven flows it's the originating
event's `MessageId`).

`UNIQUE` index on `notifications(idempotency_key)` enforces the rule
at the DB. Duplicate INSERT → catch `UniqueViolationException` → return
the existing row's Id. Same pattern as Identity's token issuance and
Orders' command handlers.

Implementation: `IdempotencyKeyGenerator`
(`src/Notifications/Notifications.Application/Common/Idempotency/`).

---

## 7. Preferences and suppression

### Preferences (per-user)

`IPreferencesService.IsAllowedAsync(userId, channel, category, ct)`
returns `PreferenceCheckResult`:

| Result | Cause |
|---|---|
| `Allow` | All gates passed |
| `Suppressed` | Global unsubscribe OR per-category opt-out |
| `RateLimited` | Hourly cap exceeded for this `(user, category)` |
| `QuietHours` | Now is between `quiet_hours_start` and `quiet_hours_end` in user's IANA timezone (and priority < Critical) |

Time math uses an injected `TimeProvider` so unit tests don't sleep.

### Suppression (hard, durable, recipient-scoped)

`ISuppressionService.IsSuppressedAsync(recipient, channel, ct)` —
hashes the canonicalised recipient (lowercase email, E.164 phone,
device-token-as-is for push) with SHA-256 and looks up the
`suppression` table. **Hash is the storage key — raw recipient never
stored in the suppression table.**

Hard bounces and complaints from delivery webhooks add automatically.
Soft bounces accumulate; only after 5 consecutive in 14 days for the
same recipient does the inbound webhook handler add to suppression.
Manual removal is operator-only with audit-log entry.

---

## 8. Templates (Scriban + MJML)

Author-time: write a template in
[Scriban](https://github.com/scriban/scriban) syntax with
[MJML](https://mjml.io/) markup for HTML email. Plain-text fallback
auto-derived.

Storage: append-only by `(template_id, version, locale)`. Activating
v4 atomically flips `is_active` from v3 to v4. Rollback is "activate
v3 again" — instant, no data loss.

Locale resolution at render time: user.locale → tenant.defaultLocale →
template's `*` fallback. A missing locale never throws — falls through
to the universal fallback.

Variable validation: every template declares `RequiredVariables`.
Render fails fast (and Notification → `Failed` status) if a caller
doesn't supply all of them.

Implementations: `ScribanTemplateRenderer`, `TemplateSelector`,
`TemplateRepository` in
`src/Notifications/Notifications.Application/Templates/` and
`Notifications.Infrastructure/Persistence/Templates/`.

---

## 9. Configuration and secrets

Per the platform's Vault wiring, every secret lives at a KV path under
`secret/notifications/`:

| Vault path | Used by | Keys |
|---|---|---|
| `secret/notifications/providers/aws-ses` | `SesEmailProvider` | `AccessKey`, `SecretKey`, `Region`, `FromAddress` |
| `secret/notifications/providers/sendgrid` | `SendGridEmailProvider` | `ApiKey`, `FromAddress` |
| `secret/notifications/providers/twilio` | `TwilioSmsProvider` | `AccountSid`, `AuthToken`, `FromNumber` |
| `secret/notifications/providers/fcm` | `FcmPushProvider` | `ProjectId`, `ServiceAccountJson` |

`Program.cs` (`src/Notifications/Notifications.Api/Program.cs`) calls
`VaultConfigBootstrap.LoadAsync` at startup with this list of paths.
Values land in `IConfiguration` so `IOptions<SesOptions>` etc. resolve
transparently. Same pattern as Identity / Payments.

DB credentials are dynamic via Vault's database secrets engine —
`DynamicCredentialsConnectionInterceptor` rotates the Postgres
username/password every 10 min. Wired via `AddVaultIntegration` in
`Notifications.Infrastructure/DependencyInjection.cs`.

On graceful shutdown the service revokes its own Vault token via
`VaultTokenRevocationHostedService` (auto-registered by
`AddVaultIntegration`).

---

## 10. Local development

Aspire AppHost (`deploy/aspire/Program.cs`) wires `notifications-svc`
with refs to Postgres, RabbitMQ, Redis, Vault. To run:

```bash
dotnet run --project deploy/aspire
```

Vault dev-mode auto-seeds the KV paths from
`infra/vault/secrets/kv-dev-values.json` (placeholder API keys — never
real). Postgres `notifications` database is created automatically.

API at `http://localhost:5260` (port from Aspire dashboard varies). All
endpoints accept `Authorization: Bearer <test-jwt>`.

To test a send:
```bash
curl -X POST http://localhost:5260/api/notifications \
  -H "Authorization: Bearer <jwt>" \
  -H "X-Idempotency-Key: test-$(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": null,
    "recipient": "test@example.com",
    "channel": 0,
    "templateId": "tpl-test",
    "priority": 2,
    "variables": {}
  }'
```

(Channel enum: 0=Email, 1=Sms, 2=Push.)

---

## 11. Production deploy

`fly.notifications.toml` + `src/Notifications/Notifications.Api/Dockerfile`
mirror `fly.payments.toml` exactly. Deployment is fully automated:

```bash
git push origin main
```

triggers `.github/workflows/deploy.yml`:
1. **Detect changed paths** via `dorny/paths-filter@v3` — push only
   triggers `notifications` matrix entry if `src/Notifications/**` or
   `fly.notifications.toml` changed.
2. **Vault deploy** if `infra/vault/**` changed (re-seeds AppRole +
   policies).
3. **Stage AppRole creds** for every service via
   `deploy/fly/ci-stage-vault-creds.sh` (response-wrapped secret-IDs).
4. **Deploy notifications-svc** via `flyctl deploy -c fly.notifications.toml --remote-only`.

App reachable internally at
`http://haworks-notifications.flycast:8080`. No public IP — only
the BFF is internet-facing. Calling services hit it via Aspire-style
service discovery (`https+http://notifications-svc`).

First-time bootstrap: an operator runs `deploy/fly/bootstrap.sh` once
to create the Fly app + stage initial secrets. Re-runs are idempotent.

---

## 12. Observability

OpenTelemetry traces propagate from caller → consumer → provider via
MassTransit headers. A support ticket "user X didn't get email Y" →
search traces by `user_id` or `idempotency_key` →
the entire span tree shows the chosen provider, the response, the
delivery webhook landing, the status transitions.

Logged structurally — never the variable values (potentially PII), only
template ID + status + latency. Provider API keys never appear in any
log path (never bound to `IConfiguration` for general use; loaded into
`IOptions<T>` from Vault and held in the provider's own field).

OTel resource: `service.name=notifications-svc`,
`service.namespace=haworks`. OTLP endpoint set as a Fly secret so
the prod cluster can swap collectors without redeploy.

---

## 13. Testing

| Suite | What it asserts | How to run |
|---|---|---|
| `tests/Notifications.Unit` (~150 tests) | Domain state-machine guards, handler logic with mocked deps, provider response mapping, gateway failover, template rendering, preferences gate matrix, suppression hash determinism | `dotnet test tests/Notifications.Unit` |
| `tests/Notifications.Integration` (5 tests) | Real Postgres via Testcontainers + WAF + mocked `IEmailProvider`. Happy-path send, suppression-blocks-send, idempotency-replay-returns-same-id, dispatch-via-provider, provider-failover-falls-through-to-secondary | `dotnet test tests/Notifications.Integration` (~60-90s on warm Docker) |

Integration tests share a single `NotificationsWebAppFactory` via
`[Collection("Notifications Integration")]` per the platform's
testing rule — one host build per `dotnet test` run, not per test
class. Per-test mocks injected via
`factory.WithWebHostBuilder(b => b.ConfigureTestServices(...))`.

---

## 14. Operational runbook (snapshot)

| Symptom | First check | Likely cause |
|---|---|---|
| "Why didn't user X get email Y" | Trace by `user_id` or recipient hash | Suppressed (hard bounce in past), or QuietHours (queued for later), or RateLimited (hit daily cap), or all-providers-exhausted |
| Bounce rate alert on template T | Pull last 100 sends from template, check pattern | Sender domain DNS drift (DKIM/SPF/DMARC), or recent template change introduced bad markup |
| Provider circuit open >5 min | Provider status page + Polly metrics | Vendor outage; gateway already failing over to secondary, but Polly takes time to fully transition |
| Customer reports "I never opted out" | `suppression` table by recipient hash | Look at `source_event_id` to find the originating bounce/complaint webhook; manual removal requires audit-log entry |
| Service won't start in prod | Fly logs | Vault reachability (secret/notifications/providers/* must exist); missing required SES/SendGrid options will throw at host build via `[Required]` + `ValidateOnStart()` |

---

## 15. What's NOT done (deliberate scope)

Per the [spec doc §1 non-goals](./notification-service.md):
- Marketing campaign orchestration (cohort builds, drip sequences) —
  belongs in a CRM.
- Rich content authoring UI — operators upload pre-rendered templates
  via the API.
- Inbound email parsing / SMS replies — separate inbound service if
  ever needed.
- Multi-region active-active — single-region prod fine until traffic
  warrants federation.
- In-app channel via SignalR — BFF owns SignalR; future BFF endpoint
  can subscribe to `NotificationDeliveredEvent` and push to clients.
- Compliance audit (CAN-SPAM/GDPR) — defer until external audit
  triggers it.

If any of these become live needs, file an ADR + brief at
`docs/agent-briefs/notifications/`.

---

## 16. References

- **Spec / design intent:** [`notification-service.md`](./notification-service.md)
- **Implementation briefs (foundation):** [`notification-service-parallel-impl.md`](../agent-briefs/notifications/notification-service-parallel-impl.md)
- **Implementation briefs (follow-ups):** [`follow-up-tracks.md`](../agent-briefs/notifications/follow-up-tracks.md)
- **Agent prompt for Gemini:** [`AGENT-PROMPT.md`](../agent-briefs/notifications/AGENT-PROMPT.md)
- **Reference services:** Identity (Vault wiring), Orders (consumers + outbox), Payments (provider abstraction)
- **Project rules:** `.claude/rules/dotnet-clean-arch.md`, `.claude/rules/event-integration-rationale.md`, `.claude/rules/resilience.md`, `.claude/rules/testing.md`
