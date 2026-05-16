# Notification Service — Follow-up Tracks

**Foundation done.** L0–L4 merged. Service builds clean, 121 unit tests
passing, integration tests refactored to share one fixture per the new
testing rule.

This brief covers what's needed to make the service **production-ready
end-to-end**: the remaining channel providers, inbound provider
webhooks, the SMS/Push channel gateways, and prod deploy wiring. Same
parallel-track model that worked for L0–L4.

**Reference:** the live service at `src/Notifications/`. Match its
patterns. Mirror existing files. Don't invent new abstractions.

---

## Pre-flight (user, ~30 sec)

Auto-merge is already enabled on the repo. Each agent claims a track,
implements, opens a PR with `--auto --squash --delete-branch`. PRs land
hands-free.

Each agent must:
- Use a dedicated worktree (`git worktree add /tmp/notif-<track>`) — the
  shared checkout still gets stomped by sibling agents.
- Use `git -C $REPO` for every git op (no persistent CWD).
- Commit per file group, push per commit.
- Read ONLY their track section in this brief plus the
  [main spec](../../architecture/notification-service.md) §3 (channels)
  and §11 (provider failover).

---

## Track table

| ID | Owns | Reference file (mirror this) | Done command |
|---|---|---|---|
| **F1** | `src/Notifications/Notifications.Infrastructure/Channels/Email/SendGrid/**` | `Channels/Email/Ses/SesEmailProvider.cs` (already merged via L2.H) | `dotnet test tests/Notifications.Unit --filter SendGridEmailProvider` returns 0 |
| **F2** | `src/Notifications/Notifications.Infrastructure/Channels/Sms/Twilio/**` + `src/Notifications/Notifications.Infrastructure/Channels/Sms/SmsChannelGateway.cs` (sibling of EmailChannelGateway) | `Channels/Email/EmailChannelGateway.cs` for the gateway shape; `Channels/Email/Ses/SesEmailProvider.cs` for the provider shape | `dotnet test tests/Notifications.Unit --filter "TwilioSmsProvider\|SmsChannelGateway"` returns 0 |
| **F3** | `src/Notifications/Notifications.Infrastructure/Channels/Push/Fcm/**` + `src/Notifications/Notifications.Infrastructure/Channels/Push/PushChannelGateway.cs` | same gateway/provider patterns as F2 | `dotnet test tests/Notifications.Unit --filter "FcmPushProvider\|PushChannelGateway"` returns 0 |
| **F4** | `src/Notifications/Notifications.Api/Webhooks/**` (new dir for inbound provider callbacks) + `tests/Notifications.Unit/Webhooks/**` | `src/Payments/Payments.Api/Webhooks/` (controllers + signature verification + idempotency) | `dotnet test tests/Notifications.Unit --filter Webhooks` returns 0 |
| **F5** | `deploy/fly/bootstrap.sh` + `fly.notifications.toml` (new) + `.github/workflows/deploy.yml` (notifications matrix entry) + `infra/vault/*` adjustments if needed | `fly.payments.toml` exactly | `flyctl config validate -c fly.notifications.toml` returns 0 + `bash -n deploy/fly/bootstrap.sh` returns 0 |

5 tracks. F1, F2, F3 can all run in parallel. F4 + F5 also independent.
F2 also lands `SmsChannelGateway`, F3 lands `PushChannelGateway` — those
are missing today (only `EmailChannelGateway` exists from L3).

---

## Universal rules (every track)

- **Branch:** `feat/notif-<track-id>` (e.g. `feat/notif-F1`).
- **Worktree:** `/tmp/notif-<track-id>`. All work happens there.
- **Forbidden files:** anything outside YOUR OWNED PATHS. If you'd need
  a cross-track edit, add `// TODO(notif-<id>): need X` and proceed
  without it.
- **csproj edits:** allowed for adding the ONE NuGet package your track
  needs. Don't restructure.
- **No new public types in shared namespaces.** Add them in your owned
  subdirectory.
- **Commit per file group + push per commit.**
- **Open PR with `gh pr merge $PR --auto --squash --delete-branch`** —
  GitHub queues + lands hands-free.

### Anti-stuck (read this)

- **No questions to user.** Decision unclear → mirror the reference file.
  Still unclear → simpler option + `// TODO(notif-<id>): revisit`.
- **Read each file at most once.** Re-reading = stuck signal.
- **60-second time-box per decision.** Over budget = mirror reference.
- **First pass = working, not perfect.** Don't refactor until "Done"
  command exits 0.
- **Build verify locally** before push: `dotnet build src/Notifications`
  must return 0. Don't push broken code expecting CI to catch.

---

## Per-track instructions

### Track F1 — SendGrid email provider

**Reference:** `src/Notifications/Notifications.Infrastructure/Channels/Email/Ses/`
(SesOptions, SesEmailProvider, SesEmailServiceCollectionExtensions).
Mirror exactly — same shape, swap SES SDK for SendGrid SDK.

**Steps:**
1. Add `SendGrid` v9.x package to `Notifications.Infrastructure.csproj`.
2. Create `SendGridOptions.cs` — properties: `ApiKey` (required),
   `FromAddress` (required). Bind from
   `Notifications:Providers:SendGrid` config section.
3. Create `SendGridEmailProvider : IEmailProvider`. `Name = "sendgrid"`.
   `SendAsync` builds a `SendGridMessage` (From + AddTo + SetSubject +
   AddContent text/html), calls `ISendGridClient.SendEmailAsync`, maps:
   - 2xx → `ProviderSendResult.Success(messageId)` (extract from
     X-Message-Id response header).
   - 429 (throttle) → `ProviderSendResult.Retryable(...)`.
   - 5xx → `ProviderSendResult.Retryable(...)`.
   - 4xx (validation, suppression) →
     `ProviderSendResult.NonRetryable(...)`.
4. Create `SendGridEmailServiceCollectionExtensions.cs` with
   `AddSendGridEmailProvider(IConfiguration)` — register `ISendGridClient`
   as Singleton + `IEmailProvider → SendGridEmailProvider` as Scoped.
   Bind `SendGridOptions` with `[Required]` + `ValidateDataAnnotations()` +
   `ValidateOnStart()`.
5. **Wire into composition root:**
   `src/Notifications/Notifications.Infrastructure/DependencyInjection.cs`
   already calls `services.AddSendGridEmailProvider(configuration)` — but
   that resolves to the L0 stub in `DependencyInjection.Stubs.cs`. Delete
   that stub line and your real method takes over (same trick L2.H used).
6. Tests: mock `ISendGridClient`. Verify request shape (To, From, Subject,
   plain + html bodies), response mapping for success/throttle/4xx/5xx.
   ~5 tests max.
7. Add `notifications/providers/sendgrid` KV path to
   `infra/vault/secrets/kv-layout.json` with keys `ApiKey`, `FromAddress`.
   Add dev placeholders to `kv-dev-values.json`.

**Done:** `dotnet test tests/Notifications.Unit --filter SendGridEmailProvider`
returns 0.

---

### Track F2 — Twilio SMS provider + SmsChannelGateway

**Reference:**
- Provider shape: `Channels/Email/Ses/SesEmailProvider.cs`.
- Gateway shape: `Channels/Email/EmailChannelGateway.cs` (Polly per-provider
  circuit breaker, iterate registered providers, record attempts).
- Channel registration: `Channels/Email/EmailChannelServiceCollectionExtensions.cs`.

**Steps:**
1. Add `Twilio` v7.x package to `Notifications.Infrastructure.csproj`.
2. Create `TwilioOptions.cs` — `AccountSid`, `AuthToken`, `FromNumber`
   (E.164 format, all required).
3. Create `TwilioSmsProvider : ISmsProvider`. `Name = "twilio"`.
   `SendAsync` calls `MessageResource.CreateAsync(to, from, body)`. Map
   Twilio error codes per
   [Twilio docs](https://www.twilio.com/docs/api/errors): 20xxx →
   `NonRetryable`, 30xxx → `NonRetryable` (carrier rejection),
   timeouts/5xx → `Retryable`.
4. Create `SmsChannelGateway : ISmsChannelGateway` in
   `Channels/Sms/SmsChannelGateway.cs`. **Copy `EmailChannelGateway.cs`
   exactly**, swap `IEmailProvider` for `ISmsProvider` and
   `IEmailChannelGateway` for `ISmsChannelGateway`. Same Polly
   circuit-breaker pattern, same failover loop, same DeliveryAttempt
   recording, same MarkSent/MarkFailed transitions.
5. Create `Channels/Sms/SmsChannelServiceCollectionExtensions.cs` with
   `AddTwilioSmsProvider(IConfiguration)` + a separate
   `AddNotificationSmsChannel()` registering the gateway.
6. **Wire into composition root:** delete the `AddTwilioSmsProvider` line
   from `DependencyInjection.Stubs.cs`. Add `services.AddNotificationSmsChannel()`
   to Infrastructure's `AddNotificationsInfrastructure` (uncomment or add
   if missing).
7. Update `NotificationDispatchConsumer` (in `Application/Consumers/`)
   so it dispatches via `ISmsChannelGateway` when `notification.Channel ==
   NotificationChannel.Sms`. Today it only handles Email. Add the switch
   case. (This is a cross-track edit — but the NotificationDispatchConsumer
   was originally L3's territory and this addition is the natural
   extension point. Add a comment marking the edit.)
8. Tests: provider response mapping (5 tests) + gateway failover
   (3 tests, mirror `EmailChannelGatewayTests`).
9. Add `notifications/providers/twilio` KV path to
   `infra/vault/secrets/kv-layout.json` with keys `AccountSid`,
   `AuthToken`, `FromNumber`.

**Done:** `dotnet test tests/Notifications.Unit --filter "TwilioSmsProvider|SmsChannelGateway"`
returns 0.

---

### Track F3 — FCM push provider + PushChannelGateway

**Reference:** F2 instructions exactly — same shape, swap SMS for Push,
Twilio for FirebaseAdmin.

**Steps:**
1. Add `FirebaseAdmin` v3.x package to `Notifications.Infrastructure.csproj`.
2. `FcmOptions` — `ProjectId`, `ServiceAccountJson` (the GCP service
   account credentials JSON, treated as a single string secret).
3. `FcmPushProvider : IPushProvider`. `Name = "fcm"`. `SendAsync` calls
   `FirebaseMessaging.SendAsync(Message)`. Map FCM exceptions:
   `INVALID_ARGUMENT`, `UNREGISTERED` → `NonRetryable`; `UNAVAILABLE`,
   `INTERNAL` → `Retryable`.
4. `PushChannelGateway : IPushChannelGateway` in
   `Channels/Push/PushChannelGateway.cs`. Copy email/sms gateway pattern.
5. `Channels/Push/PushChannelServiceCollectionExtensions.cs` with
   `AddFcmPushProvider(IConfiguration)` + `AddNotificationPushChannel()`.
6. Wire dispatch consumer for `NotificationChannel.Push`.
7. Tests: provider mapping + gateway failover (~8 tests).
8. KV path `notifications/providers/fcm` with keys `ProjectId`,
   `ServiceAccountJson`.

**Done:** `dotnet test tests/Notifications.Unit --filter "FcmPushProvider|PushChannelGateway"`
returns 0.

---

### Track F4 — Inbound delivery webhooks (SES SNS, SendGrid Event, Twilio status)

**This is a new bounded responsibility:** receive provider callbacks
about delivery status (delivered / opened / bounced / complained) and
update the corresponding `Notification` row + `Suppression` table.

**Owned paths:**
- `src/Notifications/Notifications.Api/Webhooks/**` (new dir)
- `src/Notifications/Notifications.Application/Webhooks/**` (commands + handlers)
- `tests/Notifications.Unit/Webhooks/**`

**Reference:** `src/Payments/Payments.Api/Webhooks/` for the controller +
signature-verification + idempotency-guard pattern. Same shape; different
providers.

**Endpoints:**
- `POST /api/notifications/webhooks/ses` — receives SNS messages from
  SES (subscribes to bounce, complaint, delivery topics). Verify SNS
  message signature.
- `POST /api/notifications/webhooks/sendgrid` — receives the SendGrid
  Event Webhook payload (array of events). Verify
  `X-Twilio-Email-Event-Webhook-Signature` HMAC.
- `POST /api/notifications/webhooks/twilio` — receives Twilio
  status-callback. Verify `X-Twilio-Signature`.

**Per-event handling:**
- Map provider event type → status transition
  (delivered → `MarkDelivered`, bounce → `MarkBounced` + add to suppression,
  complaint → `MarkComplained` + add to suppression).
- Idempotency: store `(provider, providerEventId)` in a dedup table
  (or reuse the existing `IWebhookIdempotencyGuard` from
  `BuildingBlocks/Webhooks/`). Replays return 200 OK without re-processing.
- Lookup target Notification by `ProviderMessageId` (must be present
  on the row from the original send).

**Tests (max 8):**
- Each provider's signature verification (good + bad).
- Each event-type → status-transition mapping (1 per provider).
- Idempotency: same event ID twice → only first updates.
- Hard bounce → suppression list updated.

**Done:** `dotnet test tests/Notifications.Unit --filter Webhooks` returns 0.

---

### Track F5 — Production deploy wiring (Fly + bootstrap.sh)

**Owned paths:**
- `fly.notifications.toml` (CREATE)
- `deploy/fly/bootstrap.sh` (small surgical edits)
- `.github/workflows/deploy.yml` (add notifications to the matrix)

**Reference:** `fly.payments.toml` exactly. Mirror its structure: same
build context (Dockerfile path), same internal_port, same memory,
same `[deploy]` strategy.

**Steps:**
1. Create `fly.notifications.toml`. Copy `fly.payments.toml`. Change
   `app = "haworks-notifications"`. Change Dockerfile to point at
   the existing pattern (likely `Dockerfile.notifications` — create a
   minimal one mirroring `Dockerfile.payments` if absent: ASP.NET 9
   runtime + COPY published output).
2. `deploy/fly/bootstrap.sh`: add `haworks-notifications` to the
   `INTERNAL_APPS` array. The common[] env vars + Vault staging will
   then automatically apply.
3. `.github/workflows/deploy.yml`: add `notifications` to the `services`
   matrix in the `plan` job. The existing build/deploy machinery picks
   it up automatically.
4. `infra/vault/services.json` already has `notifications` in the list
   (added in L0). `infra/vault/database/roles.json` also has
   `haworks-notifications`. No change needed.
5. Add a Dockerfile for notifications-svc if absent. Mirror
   `src/Payments/Payments.Api/Dockerfile` (or whichever pattern the
   other services use — check `fly.payments.toml`'s `[build]` section).

**Done:**
- `flyctl config validate -c fly.notifications.toml` returns 0
- `bash -n deploy/fly/bootstrap.sh` returns 0
- `gh pr create` opens cleanly (workflow YAML valid)

---

## Coordination

After all 5 tracks merge:

1. Run the integration test suite end-to-end: `dotnet test tests/Notifications.Integration`. Should be green (host builds + tests pass).
2. Smoke-test prod deploy: `gh workflow run deploy.yml --ref main` and verify the notifications-svc Fly app comes up healthy.
3. Send a real test notification via the new prod endpoint. Verify the SES delivery webhook lands and updates the Notification row to `Delivered`.

These are validation steps for the user/operator, not agent work.

---

## What's NOT in this brief (deliberate scope cuts)

- **Multi-region active-active** (spec §10) — single-region prod is fine
  for now. Federation + Postgres replicas come when traffic warrants.
- **Compliance audit** (spec §14) — defer until first external audit
  triggers it.
- **In-app channel via SignalR** — the BFF already owns SignalR; this
  is a thin BFF endpoint that subscribes to `NotificationDeliveredEvent`
  and pushes to connected clients. Future BFF track, not Notifications.
- **Marketing-campaign orchestration** (spec non-goals §1) — explicitly
  out of scope.
- **Rich content templates / WYSIWYG** — operators upload pre-rendered
  Scriban+MJML via the existing template API.

---

## Done = ready for first prod traffic

Once F1-F5 merge:
- 3 channels live (Email, SMS, Push) with provider failover
- 4 inbound provider webhooks updating delivery state
- Service deploys cleanly to Fly via `git push`
- Operator can send a real notification and watch it through to Delivered
