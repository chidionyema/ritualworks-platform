# Financial Service Hardening Templates

**Mandatory reference for all Payments, Payouts, Checkout, and Ledger code.**
Every handler and consumer touching money MUST follow one of these two templates.

---

## Template 1: Outbound Gateway Orchestrator (MediatR Handler)

Use when: An action requires calling an external API (Stripe, PayPal, payout gateway)
while maintaining local database integrity.

**Three-phase execution prevents Dual-Write and Check-Then-Act bugs.**

```csharp
public sealed class ProcessExternalActionCommandHandler(
    IYourDbContext context,
    IExternalGateway externalGateway,
    Polly.IAsyncPolicy resiliencePolicy,
    ILogger<ProcessExternalActionCommandHandler> logger) : IRequestHandler<ProcessExternalActionCommand, bool>
{
    public async Task<bool> Handle(ProcessExternalActionCommand request, CancellationToken ct)
    {
        // =====================================================================
        // PHASE 1: ATOMIC LOCAL RESERVATION & IDEMPOTENCY CHECK
        // =====================================================================
        var executionStrategy = context.Database.CreateExecutionStrategy();
        Guid pendingActionId = Guid.Empty;

        await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(ct);

            // Pessimistic lock prevents rapid double-clicks
            var entity = await context.YourEntities
                .FromSqlRaw(@"SELECT * FROM schema.""YourEntities"" WHERE ""Id"" = {0} FOR UPDATE", request.TargetId)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Entity not found.");

            // Idempotency check INSIDE the lock boundary
            if (entity.HasAlreadyProcessed(request.IdempotencyKey))
            {
                pendingActionId = entity.GetActionId(request.IdempotencyKey);
                await tx.RollbackAsync(ct);
                return;
            }

            // Mutate domain state to "Pending" and lock the funds
            var pendingAction = entity.PrepareAction(request.Amount, request.IdempotencyKey);
            pendingActionId = pendingAction.Id;

            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        // =====================================================================
        // PHASE 2: RESILIENT EXTERNAL I/O (OUTSIDE Database Locks)
        // =====================================================================
        var isGatewaySuccess = false;
        string? externalTransactionId = null;
        string? failureReason = null;

        try
        {
            // Polly retries. IdempotencyKey passed to provider for safe retries.
            // NO try/catch inside the delegate — let Polly see exceptions.
            (externalTransactionId, isGatewaySuccess) = await resiliencePolicy.ExecuteAsync(
                () => externalGateway.DispatchAsync(request.Amount, request.IdempotencyKey, ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gateway failed for key {Key}", request.IdempotencyKey);
            failureReason = ex.Message;
        }

        // =====================================================================
        // PHASE 3: RESOLUTION & OUTBOX RECONCILIATION
        // =====================================================================
        await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(ct);

            var entity = await context.YourEntities
                .FromSqlRaw(@"SELECT * FROM schema.""YourEntities"" WHERE ""Id"" = {0} FOR UPDATE", request.TargetId)
                .FirstAsync(ct);

            if (isGatewaySuccess)
            {
                entity.ConfirmAction(pendingActionId, externalTransactionId!);
                // Event saved atomically via MassTransit EF Outbox
                await context.PublishEventAsync(
                    new ActionCompletedIntegrationEvent(entity.Id, request.Amount), ct);
            }
            else
            {
                entity.RollbackAction(pendingActionId,
                    failureReason ?? "Gateway rejected transaction");
            }

            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        return isGatewaySuccess;
    }
}
```

### Rules enforced by this template:

| Rule | How |
|------|-----|
| No Dual-Write | Event published inside same SaveChanges as state via Outbox |
| No Check-Then-Act | Idempotency check inside FOR UPDATE lock |
| No Swallowed Exceptions | No try/catch inside Polly delegate |
| No Double-Charge | IdempotencyKey sent to external provider |
| Rollback on Gateway Failure | Phase 3 reverses Phase 1 if gateway fails |

---

## Template 2: Idempotent Event Consumer (MassTransit)

Use when: Processing async events that update local state or financial ledgers.
Handles duplicate messages, out-of-order delivery, and concurrent consumers.

```csharp
public class IdempotentFundsConsumer(
    IYourDbContext context,
    ILogger<IdempotentFundsConsumer> logger) : IConsumer<FundsTriggeredEvent>
{
    public async Task Consume(ConsumeContext<FundsTriggeredEvent> consumeContext)
    {
        var evt = consumeContext.Message;

        // 1. SINGLE-TRIP DETERMINISTIC QUERY
        // Filter by EntryType AND AccountType to prevent wrong-account selection
        var accountDetails = await context.LedgerEntries
            .AsNoTracking()
            .Where(e => e.ReferenceId == evt.SourceId.ToString()
                     && e.Type == EntryType.Credit)
            .Select(e => new
            {
                e.Account.OwnerId,
                e.AccountId,
                AccountType = e.Account.Type
            })
            .FirstOrDefaultAsync(
                e => e.AccountType == AccountType.SellerPayable,
                consumeContext.CancellationToken);

        if (accountDetails == null)
        {
            logger.LogWarning("No seller credit for {SourceId} — skipping", evt.SourceId);
            return;
        }

        // 2. UNIQUE IDEMPOTENCY REFERENCE
        var uniqueRef = $"FUNDS-MATURED:{evt.SourceId}";

        try
        {
            // 3. ATOMIC LEDGER OPERATION INSIDE TRANSACTION
            await ApplyAtomicShiftAsync(
                accountDetails.AccountId, evt.Amount, uniqueRef,
                consumeContext.CancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 4. CONCURRENT DUPLICATE TRAP — safe swallow
            logger.LogWarning(ex, "Duplicate {Ref} caught at DB index — idempotent skip", uniqueRef);
        }
    }

    private async Task ApplyAtomicShiftAsync(
        Guid accountId, decimal amount, string referenceKey, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(ct);

            // Double-check inside transaction for sequential redeliveries
            if (await context.LedgerEntries.AnyAsync(e => e.ReferenceId == referenceKey, ct))
                return;

            var account = await context.LedgerAccounts.FindAsync([accountId], ct);
            account!.UpdateBalance(amount, EntryType.Debit);

            context.LedgerEntries.Add(LedgerEntry.Create(
                accountId, Guid.NewGuid(), amount, EntryType.Debit,
                "Maturity shift", referenceKey));

            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // Postgres error 23505 = unique_violation
        return ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
    }
}
```

### Rules enforced by this template:

| Rule | How |
|------|-----|
| No Double-Credit/Debit | Unique index on ReferenceId catches concurrent duplicates |
| No Wrong-Account Selection | Explicit EntryType + AccountType filter on queries |
| No Check-Then-Act Gap | Idempotency check inside transaction boundary |
| Sequential Dedup | AnyAsync inside transaction catches sequential replays |
| Concurrent Dedup | DB unique constraint catches racing threads |
| Deterministic Queries | Explicit filters, no bare FirstOrDefaultAsync |

---

## Anti-Patterns (NEVER DO THIS)

```csharp
// ❌ NEVER: try/catch inside Polly delegate
await policy.ExecuteAsync(async () => {
    try { return await stripe.CreateAsync(...); }
    catch (StripeException) { return new FailedResult(); } // Polly can't retry!
});

// ❌ NEVER: Check-then-act outside transaction
var exists = await db.AnyAsync(e => e.Ref == id);  // Thread B reads here too
if (exists) return;
await db.AddAsync(new Entry(...));                   // Both threads insert!

// ❌ NEVER: Event publish without SaveChanges
await eventPublisher.PublishAsync(new RefundEvent(...));
// No SaveChangesAsync! Outbox never commits. Or event escapes without DB state.

// ❌ NEVER: Bare FirstOrDefaultAsync on multi-row tables
var entry = await db.LedgerEntries
    .FirstOrDefaultAsync(e => e.ReferenceId == paymentId);
// Returns random row — could be platform fee instead of seller credit!

// ❌ NEVER: Take() without OrderBy()
var batch = await db.Accounts.Where(...).Take(500).ToListAsync();
// Non-deterministic — some records may never be processed!

// ❌ NEVER: Trust caller for idempotency key on financial operations
var key = request.IdempotencyKey; // Could be null!
// ALWAYS: var key = request.IdempotencyKey ?? Guid.NewGuid().ToString();
```

---

## Applicability

These templates apply to ALL services, not just Payments:
- **Catalog**: StockReservation/Release consumers
- **Orders**: RefundCompleted/Cancelled consumers
- **Checkout**: Saga state transitions
- **Payouts**: All ledger operations + disbursement
- **Merchant**: Registration + activation
- **Privacy**: Erasure request handling
- **Notifications**: Webhook-validated consumers
- **Media**: Upload processing consumers
