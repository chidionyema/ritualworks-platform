# Wave 2 — Deep Audit Fix Specification

> Generated 2026-05-15. Covers concurrency, data integrity, resilience, and business logic.
> Every item: exact file:line, root cause, fix with code, test spec.
> Severity: CRITICAL > HIGH > MEDIUM > LOW

---

## CRITICAL

### C2-01: Payouts — LedgerAccount.Balance has no concurrency token (lost updates)
- **File**: `src/Payouts/Payouts.Infrastructure/Persistence/PayoutsDbContext.cs` (LedgerAccount config)
- **Root cause**: `LedgerAccount` inherits `AuditableEntity` with `RowVersion` but no `.IsConcurrencyToken()` call in OnModelCreating. Concurrent `CreditSellerAsync` calls produce lost-update races on Balance.
- **Fix**: In `PayoutsDbContext.OnModelCreating`, add to LedgerAccount config:
  ```csharp
  entity.Property<uint>("xmin")
      .HasColumnType("xid")
      .ValueGeneratedOnAddOrUpdate()
      .IsConcurrencyToken();
  ```
- **Test spec**:
  - `Concurrent_credits_to_same_seller_are_serialized_by_concurrency_token` — seed account, run 2 concurrent `CreditSellerAsync` calls via `Task.WhenAll`, verify final balance equals sum of both credits (not just the last one)

### C2-02: Payouts — Commission never deducted (platform earns $0)
- **File**: `src/Payouts/Payouts.Application/Ledger/Services/LedgerService.cs:30`
- **Root cause**: `CreditSellerAsync` credits the full `amount` to seller. `SellerProfile.CommissionPercentage` (default 10%) is never consulted.
- **Fix**: In `CreditSellerAsync`, after loading seller profile, calculate commission:
  ```csharp
  var profile = await _context.SellerProfiles.FirstOrDefaultAsync(s => s.Id == sellerId, ct);
  var commissionRate = profile?.CommissionPercentage ?? 10.00m;
  var commission = Math.Round(amount * commissionRate / 100m, 2, MidpointRounding.AwayFromZero);
  var sellerAmount = amount - commission;
  // Credit seller with sellerAmount, credit platform with commission
  var sellerEntry = LedgerEntry.Create(sellerAccount.Id, transactionId, sellerAmount, EntryType.Credit, ...);
  var platformEntry = LedgerEntry.Create(platformAccount.Id, transactionId, commission, EntryType.Credit, ...);
  ```
- **Test spec**:
  - `CreditSellerAsync_deducts_10_percent_commission_by_default` — credit 100.00, verify seller gets 90.00, platform gets 10.00
  - `CreditSellerAsync_uses_custom_commission_from_seller_profile` — set profile to 15%, credit 100, verify 85/15 split

### C2-03: Payouts — DisbursementService double-payout race (stale read + non-atomic debit)
- **File**: `src/Payouts/Payouts.Application/Disbursements/Services/DisbursementService.cs:43-69`
- **Root cause**: Reads balance, writes Payout row, calls Stripe, then debits balance in separate SaveChangesAsync. Process crash between Stripe success and debit = money sent but not debited. No concurrency token = two concurrent runs = double payout.
- **Fix**: Wrap entire operation in a transaction with row-level lock:
  ```csharp
  await using var transaction = await _context.Database.BeginTransactionAsync(ct);
  // Re-read balance inside transaction
  var account = await _context.LedgerAccounts
      .FromSqlRaw("SELECT * FROM ledger_accounts WHERE id = {0} FOR UPDATE", accountId)
      .FirstAsync(ct);
  // ... create payout, call gateway, debit, save all in one transaction
  await transaction.CommitAsync(ct);
  ```
- **Test spec**:
  - `Concurrent_disbursements_for_same_seller_only_pays_once` — seed balance 100, run 2 concurrent `ExecutePayout`, verify only 1 Stripe call and balance = 0

### C2-04: Payouts — PaymentCompletedConsumer no inbox (double-credit on redelivery)
- **File**: `src/Payouts/Payouts.Infrastructure/DependencyInjection.cs:22-31`
- **Root cause**: MassTransit configured without `AddEntityFrameworkOutbox`. No inbox deduplication. Redelivered `PaymentCompletedEvent` credits seller twice.
- **Fix**: Add outbox + inbox:
  ```csharp
  mt.AddEntityFrameworkOutbox<PayoutsDbContext>(o =>
  {
      o.UsePostgres();
      o.UseBusOutbox();
      o.QueryDelay = TimeSpan.FromSeconds(1);
  });
  ```
  Add to `PayoutsDbContext.OnModelCreating`:
  ```csharp
  modelBuilder.AddInboxStateEntity();
  modelBuilder.AddOutboxStateEntity();
  modelBuilder.AddOutboxMessageEntity();
  ```
  Create new EF migration for outbox tables.
- **Test spec**:
  - `PaymentCompletedEvent_redelivered_is_idempotent` — publish same event twice, verify seller credited only once

### C2-05: Payment.UserId stores customer email, not actual userId
- **File**: `src/Payments/Payments.Application/Consumers/PaymentSessionRequestedConsumer.cs:74`
- **Root cause**: `Payment.Create(..., evt.CustomerEmail, ...)` passes email as userId. `PaymentSessionRequestedEvent` has no `UserId` field.
- **Fix**: Add `string UserId` to `PaymentSessionRequestedEvent` contract. In `CheckoutSaga`, populate it from `ctx.Saga.UserId`. In consumer, use `evt.UserId` instead of `evt.CustomerEmail`.
- **Test spec**:
  - `Payment_stores_userId_not_email` — publish event with UserId="user-123" and Email="a@b.com", verify Payment.UserId == "user-123"

### C2-06: Catalog — Stock double-reserve (existence check outside transaction)
- **File**: `src/Catalog/Catalog.Infrastructure/Services/StockService.cs:22-28`
- **Root cause**: `FirstOrDefaultAsync` check for existing reservation runs BEFORE `BeginTransactionAsync`. Two concurrent calls both see null, both proceed to reserve.
- **Fix**: Move the existence check inside the transaction:
  ```csharp
  await using var transaction = await db.Database.BeginTransactionAsync(ct);
  var existing = await db.StockReservations.FirstOrDefaultAsync(r => r.OrderId == orderId, ct);
  if (existing != null) { await transaction.CommitAsync(ct); return Result.Success(); }
  // ... proceed with reservation
  ```
  Also add unique index on `StockReservations.OrderId`.
- **Test spec**:
  - `Concurrent_reservations_for_same_order_only_reserves_once` — `Task.WhenAll` two reserve calls, verify stock decremented once

---

## HIGH

### H2-01: Catalog — Stock double-release (sweeper + consumer race, no concurrency token)
- **File**: `src/Catalog/Catalog.Infrastructure/Services/StockService.cs:91-123`
- **Root cause**: `ReleaseStockAsync` checks `reservation.ReleasedAt.HasValue` (TOCTOU) then increments stock unconditionally. No concurrency token on StockReservation. Two callers both pass guard and double-increment stock.
- **Fix**: Add `xmin` concurrency token to StockReservation in CatalogDbContext. Add WHERE clause to stock increment: only increment if reservation is not already released (atomic check).
- **Test spec**:
  - `Concurrent_stock_release_for_same_reservation_only_releases_once`

### H2-02: Catalog — IsInStock uses pre-update stock value
- **File**: `src/Catalog/Catalog.Infrastructure/Services/StockService.cs:52`
- **Root cause**: `.SetProperty(p => p.IsInStock, p => p.StockQuantity > item.Quantity)` evaluates against old row values in PostgreSQL UPDATE SET.
- **Fix**: Change to `.SetProperty(p => p.IsInStock, p => p.StockQuantity - item.Quantity > 0)`
- **Test spec**:
  - `ReserveStock_sets_IsInStock_false_when_exact_quantity_reserved` — product with stock=5, reserve 5, verify IsInStock=false

### H2-03: Stripe amount truncation in 3 locations
- **Files**:
  - `src/Payouts/Payouts.Infrastructure/Gateways/StripePayoutGateway.cs:54`
  - `src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs:123`
  - `src/Payments/Payments.Application/Consumers/ProviderRefundInitiationRequestedConsumer.cs:42`
- **Root cause**: `(long)(amount * 100)` truncates instead of rounding. `10.999m * 100 = 1099` not `1100`.
- **Fix**: Replace all 3 with `(long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero)`
- **Test spec**:
  - `Stripe_amount_conversion_rounds_correctly_for_fractional_cents` — verify 10.999m → 1100L, 10.001m → 1000L

### H2-04: CheckoutSaga — Currency hardcoded to "USD"
- **File**: `src/CheckoutOrchestrator/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs:88,100`
- **Root cause**: `sagaState.Currency = "USD"` regardless of actual currency.
- **Fix**: Add `Currency` field to `CheckoutInitiatedEvent`. In saga, use `ctx.Message.Currency ?? "USD"` as fallback.
- **Test spec**:
  - `Checkout_preserves_currency_from_initiated_event` — initiate with EUR, verify saga state and downstream events carry EUR

### H2-05: Orders — CheckoutSessionExpiredConsumer outbox not persisted
- **File**: `src/Orders/Orders.Application/Consumers/CheckoutSessionExpiredConsumer.cs:51-84`
- **Root cause**: `MarkStockReleasedAsync` uses `ExecuteUpdateAsync` (commits immediately). `eventPublisher.PublishAsync` writes to outbox but `SaveChangesAsync` is never called — outbox message lost, stock never released.
- **Fix**: Add `await db.SaveChangesAsync(ct);` after `eventPublisher.PublishAsync` to flush the outbox.
- **Test spec**:
  - `CheckoutSessionExpired_publishes_StockReleaseRequestedEvent` — expire a session, verify event is published and consumed

### H2-06: Webhooks — No inbox, no unique constraint on (SubscriptionId, EventId)
- **File**: `src/Webhooks/Webhooks.Infrastructure/Persistence/WebhooksDbContext.cs`
- **Fix**: Add unique index: `entity.HasIndex(e => new { e.SubscriptionId, e.EventId }).IsUnique();`. Add outbox/inbox to Webhooks DependencyInjection.
- **Test spec**:
  - `Duplicate_event_delivery_is_rejected_by_unique_constraint`

### H2-07: RefundSaga — RequiresReview is a dead end (no approval path)
- **File**: `src/Payments/Payments.Application/Sagas/RefundSaga.cs:80,114,127`
- **Fix**: Add `RefundApprovedByOperator` event. Add handler in `During(RequiresReview)` that publishes `ProviderRefundInitiationRequestedEvent` and transitions to `AwaitingProviderConfirmation`.
- **Test spec**:
  - `RefundApprovedByOperator_in_RequiresReview_resumes_refund_flow`

### H2-08: Notifications — Refund emails go to customer@example.com
- **File**: `src/Notifications/Notifications.Application/Consumers/RefundEmailConsumer.cs:26,44`
- **Root cause**: Hardcoded `Recipient: "customer@example.com"`.
- **Fix**: Use `msg.CustomerEmail` from the event. Add `CustomerEmail` to `RefundCompletedEvent` and `RefundFailedEvent` contracts if missing.
- **Test spec**:
  - `RefundCompleted_sends_email_to_actual_customer`

### H2-09: Kafka CDC workers — infinite retry on malformed messages
- **File**: `src/Search/Search.Application/Consumers/IndexableEntityChangedConsumer.cs:42-50`
- **Root cause**: Exception caught, logged, but offset not committed. Same bad message reprocessed forever.
- **Fix**: On non-transient exceptions (JsonException, KeyNotFoundException, FormatException), commit the offset and skip:
  ```csharp
  catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException)
  {
      logger.LogError(ex, "Skipping malformed CDC message on {Topic}", result.Topic);
      consumer.Commit(result); // skip poison message
  }
  ```
  Apply same pattern to `BffCdcCacheInvalidator` and `CdcFanOutWorker`.
- **Test spec**:
  - `Malformed_CDC_message_is_skipped_not_retried_forever`

### H2-10: CDC SearchIndexWorker — isInStock/isListed hardcoded to true
- **File**: `src/Search/Search.Application/Consumers/IndexableEntityChangedConsumer.cs:105-106`
- **Root cause**: `isInStock: true, isListed: true` regardless of actual DB values.
- **Fix**: Read `is_in_stock` and `is_listed` from the Debezium `after` JSON element.
- **Test spec**:
  - `CDC_product_update_with_out_of_stock_sets_IsInStock_false`

---

## MEDIUM

### M2-01: PayoutsDbContext uses public schema (collision risk)
- **File**: `src/Payouts/Payouts.Infrastructure/Persistence/PayoutsDbContext.cs`
- **Fix**: Add `modelBuilder.HasDefaultSchema("payouts");` in OnModelCreating. Create migration.

### M2-02: Notification entity no concurrency token (double-send race)
- **File**: `src/Notifications/Notifications.Infrastructure/Persistence/NotificationsDbContext.cs:51-80`
- **Fix**: Add `xmin` concurrency token to Notification entity config.

### M2-03: WebhookDelivery no concurrency token (Hangfire double-dispatch)
- **File**: `src/Webhooks/Webhooks.Infrastructure/Persistence/WebhooksDbContext.cs:32-49`
- **Fix**: Add `xmin` concurrency token to WebhookDelivery entity config.

### M2-04: SubscriptionSaga dunning exhaustion emits no cancellation event
- **File**: `src/Payments/Payments.Application/Sagas/SubscriptionSaga.cs:142-157`
- **Fix**: Before `TransitionTo(Canceled).Finalize()`, add `.PublishAsync(ctx => ctx.Init<SubscriptionCancelledEvent>(...))`.

### M2-05: CDC HandleCategoryChangeAsync is a no-op stub
- **File**: `src/Search/Search.Application/Consumers/IndexableEntityChangedConsumer.cs:115-128`
- **Fix**: Implement actual re-denormalization (query index for products with that categoryId, upsert with new name). Match the pattern in `CategoryUpdatedConsumer`.

### M2-06: Webhook dispatch retry off-by-one
- **File**: `src/Webhooks/Webhooks.Infrastructure/Hangfire/WebhookDispatcher.cs:71`
- **Root cause**: `CalculateNextAttempt(delivery.Attempts)` called before `RecordAttempt` increments `Attempts`.
- **Fix**: Move `CalculateNextAttempt` call to after `RecordAttempt`.

### M2-07: SubscriptionSaga GuardDelay(TimeSpan.Zero) fires immediately for past-PeriodEnd
- **File**: `src/Payments/Payments.Application/Sagas/SubscriptionSaga.cs:174-175`
- **Fix**: Set minimum delay: `delay < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay`

### M2-08: PaymentDbContext.OnBeforeSaving doesn't stamp LastModifiedDate
- **File**: `src/Payments/Payments.Infrastructure/PaymentDbContext.cs:168-178`
- **Fix**: Add `Modified` entity state handling matching CatalogDbContext/OrderDbContext pattern.

### M2-09: Audit COPY batch silently dropped on PostgresException
- **File**: `src/Audit/Audit.Infrastructure/Persistence/AuditWriter.cs:56-84`
- **Fix**: On failure, re-queue the batch items back to the channel instead of clearing. Add retry count and dead-letter after 3 failures.

### M2-10: StockReservation no concurrency token (sweeper double-expiry)
- **File**: `src/Catalog/Catalog.Infrastructure/CatalogDbContext.cs:163-185`
- **Fix**: Add `xmin` concurrency token to StockReservation entity config.

### M2-11: Payment.OrderId no unique constraint (duplicate payments on retry)
- **File**: `src/Payments/Payments.Infrastructure/PaymentDbContext.cs:65`
- **Fix**: Change to `.HasIndex(p => p.OrderId).IsUnique()`. Handle `DbUpdateException` in consumer.

### M2-12: Tax always 0m in payment creation
- **File**: `src/Payments/Payments.Application/Consumers/PaymentSessionRequestedConsumer.cs:76`
- **Fix**: Add `Tax` field to `PaymentSessionRequestedEvent`. Populate from checkout data.

### M2-13: ProviderRefundCancellationRequestedEvent has zero consumers (dead event)
- **File**: `src/Payments/Payments.Application/Sagas/RefundSaga.cs:138`
- **Fix**: Create `ProviderRefundCancellationConsumer` in Payments.Application that calls `gateway.Refunds.CancelRefundAsync()`.

### M2-14: Hangfire DisbursementService + MatureFundsCommand no mutual exclusion
- **File**: `src/Payouts/Payouts.Api/Program.cs:43-48`
- **Fix**: Use `[DisableConcurrentExecution(timeoutInSeconds: 300)]` attribute on both job methods, or use Hangfire's `[AutomaticRetry(Attempts = 0)]` + distributed lock.
