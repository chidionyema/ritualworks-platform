# Integration Test Backlog — Path to Strong Coverage

> Target: Every service at 10+ integration test files (matching Catalog/Payments/Search)
> Current: 7 services below 5 test files, 1 service at zero

## Priority 1 — Zero Coverage (blocks prod confidence)

### Pricing (0 test files → target 10)

| Test | What it validates | Effort |
|------|------------------|--------|
| `PriceCalculation_ReturnsCorrectTotal` | GET /calculate with quantity, base price, tax rate | S |
| `PriceCalculation_AppliesDiscountRules` | GET /calculate with active PriceRule in DB | S |
| `PromotionValidate_ValidCode_ReturnsDiscount` | POST /promotions/validate with seeded promotion | S |
| `PromotionRedeem_ValidCode_MarksUsed` | POST /promotions/redeem, verify DB state | S |
| `PromotionRedeem_ExpiredCode_Returns400` | POST /promotions/redeem with past-expiry code | S |
| `TaxRate_ReturnsConfiguredRate` | GET /tax/rate for known jurisdiction | S |
| `PricingRequestedConsumer_PublishesPriceCalculatedEvent` | MassTransit harness: publish PricingRequestedEvent, verify PriceCalculatedEvent | M |
| `ProductCacheInvalidatedConsumer_ClearsCache` | Publish invalidation, verify cache miss on next GET | M |
| `CRUD_PriceRules_EndToEnd` | POST create, GET read, DELETE remove | S |
| `ConcurrentPromotionRedeem_OnlyOneSucceeds` | Two concurrent redeems on same single-use code | M |

## Priority 2 — Light Coverage (3 test files → target 8+)

### FeatureFlags (3 → 8)

| Test | What it validates |
|------|------------------|
| `Evaluate_EnabledFlag_ReturnsTrue` | GET /evaluate with flag=on |
| `Evaluate_DisabledFlag_ReturnsFalse` | GET /evaluate with flag=off |
| `Evaluate_PercentageRollout_RespectsThreshold` | GET /evaluate with 50% rollout, verify distribution |
| `Update_Flag_PublishesEvent` | POST /update, verify FeatureFlagUpdated event |
| `Delete_Flag_Returns204` | DELETE /{name} |

### Location (3 → 7)

| Test | What it validates |
|------|------------------|
| `CreateLocation_PersistsGeoPoint` | POST with lat/lng, verify PostGIS storage |
| `NearbySearch_ReturnsWithinRadius` | Seed 3 points, GET /nearby with radius, verify correct subset |
| `NearbySearch_EmptyRadius_Returns400` | Validation |
| `NearbySearch_OrdersByDistance` | Verify result ordering |

### Scheduler (3 → 7)

| Test | What it validates |
|------|------------------|
| `ScheduleJob_PersistsToHangfire` | POST /schedule, verify Hangfire storage |
| `LeaseWatcherJob_RotatesExpiringCredentials` | Seed expired lease, run job, verify rotation event |
| `SecretExpiryWatcher_PublishesWarning` | Seed near-expiry secret, run job, verify event |
| `RotateJwtKeyJob_WritesNewKey` | Run job, verify Vault KV updated |

### Webhooks (3 → 10)

| Test | What it validates |
|------|------------------|
| `CreateWebhook_PersistsSubscription` | POST, verify DB row |
| `CreateWebhook_InvalidUrl_Returns400` | Validation |
| `ReplayWebhook_ResendsPayload` | POST /{id}/replay, verify HTTP call |
| `RotateSecret_UpdatesHmacKey` | POST /{id}/rotate-secret, verify new signature |
| `OrderCompletedConsumer_DispatchesToSubscribers` | Publish OrderCompletedEvent, verify fanout |
| `PaymentCompletedConsumer_DispatchesToSubscribers` | Same pattern |
| `DeliveryAttempt_Records_SuccessAndFailure` | Verify attempt log after delivery |

## Priority 3 — Moderate Coverage (4-5 test files → target 8+)

### Analytics (4 → 8)

| Test | What it validates |
|------|------------------|
| `IngestEvent_PersistsToStore` | POST with event payload |
| `IngestEvent_InvalidPayload_Returns400` | Validation |
| `BatchIngest_ProcessesAll` | POST with array of events |
| `QueryEvents_FiltersByDateRange` | Seed events, query with date filter |

### Media (4 → 10)

| Test | What it validates |
|------|------------------|
| `InitiateUpload_ReturnsPresignedUrl` | POST /initiate, verify S3 presigned URL |
| `CompleteUpload_MovesFromQuarantine` | POST /{id}/complete, verify S3 move |
| `CompleteUpload_ClamAVReject_Returns422` | Infected file rejected |
| `AbortUpload_CleansUpS3` | POST /{id}/abort, verify S3 deletion |
| `BatchInitiate_ReturnsMultipleUrls` | POST /batch-initiate |
| `ProcessMediaConsumer_GeneratesThumbnail` | Publish ProcessMediaCommand, verify output |

### Merchant (4 → 8)

| Test | What it validates |
|------|------------------|
| `CreateMerchant_PendingState` | POST, verify status=Pending |
| `ApproveMerchant_TransitionsToActive` | POST /{id}/approve |
| `SuspendMerchant_TransitionsToSuspended` | POST /{id}/suspend |
| `GetBySlug_ReturnsCorrectMerchant` | GET /by-slug/{slug} |

### Privacy (4 → 8)

| Test | What it validates |
|------|------------------|
| `CreateErasureRequest_PersistsWithDeadline` | POST, verify 30-day GDPR deadline |
| `GetRequest_ReturnsStatus` | GET /{id} |
| `ErasureRequest_PublishesEvent` | Verify PrivacyErasureRequested published |
| `DuplicateRequest_Returns409` | Same userId, same active request |

### RulesEngine (4 → 8)

| Test | What it validates |
|------|------------------|
| `CreateRule_Persists` | POST with rule definition |
| `EvaluateRule_MatchesCondition` | POST /evaluate with matching input |
| `EvaluateRule_NoMatch_ReturnsEmpty` | POST /evaluate with non-matching input |
| `FraudCheckConsumer_PublishesFraudResult` | Publish FraudCheckRequestedEvent, verify response |

### Identity (5 → 10)

| Test | What it validates |
|------|------------------|
| `Register_CreatesUser_ReturnsTokens` | Full registration flow |
| `Login_ValidCredentials_ReturnsJwt` | Authentication flow |
| `Login_InvalidPassword_Returns401` | Failure path |
| `RefreshToken_ReturnsNewPair` | Token refresh flow |
| `JwtKeyRotatedConsumer_UpdatesSigningKey` | Publish rotation event, verify new key active |

### Notifications (6 → 10)

| Test | What it validates |
|------|------------------|
| `CreateNotification_PersistsAndPublishes` | POST, verify DB + event |
| `RefundCompletedConsumer_SendsEmail` | Publish event, verify notification created |
| `SecretExpiryWarningConsumer_AlertsOps` | Publish warning, verify notification |
| `SesWebhook_ProcessesBounce` | POST /ses with bounce payload |

### Localization (4 → 7)

| Test | What it validates |
|------|------------------|
| `GetKey_ReturnsTranslation` | GET /{key} with seeded data |
| `GetKey_MissingKey_ReturnsFallback` | Fallback behavior |
| `PutKey_UpdatesTranslation` | PUT /{key}, verify updated |

## Tracking

| Service | Current | Target | Gap | Priority |
|---------|---------|--------|-----|----------|
| Pricing | 0 | 10 | 10 | P1 |
| FeatureFlags | 3 | 8 | 5 | P2 |
| Location | 3 | 7 | 4 | P2 |
| Scheduler | 3 | 7 | 4 | P2 |
| Webhooks | 3 | 10 | 7 | P2 |
| Analytics | 4 | 8 | 4 | P3 |
| Localization | 4 | 7 | 3 | P3 |
| Media | 4 | 10 | 6 | P3 |
| Merchant | 4 | 8 | 4 | P3 |
| Privacy | 4 | 8 | 4 | P3 |
| RulesEngine | 4 | 8 | 4 | P3 |
| Identity | 5 | 10 | 5 | P3 |
| Notifications | 6 | 10 | 4 | P3 |
| **Total gap** | | | **64 tests** | |

## Done

| Service | Date | PR |
|---------|------|----|
| (none yet) | | |
