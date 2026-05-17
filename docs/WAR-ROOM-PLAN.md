# War Room — Financial Services Deep Audit Remediation Plan

## Complete Issue Registry (Every Issue from Gemini 12-Lens Audit)

### Category A: Polly Retry Bugs (Swallowed Exceptions)
| # | File | Issue | Severity |
|---|------|-------|----------|
| A1 | StripeRefundService.cs | try/catch inside ExecuteAsync prevents Polly retries | CRITICAL |
| A2 | StripePaymentProcessor.cs (ValidateSessionAsync) | Same pattern — catch returns false, Polly never retries | CRITICAL |
| A3 | StripeSubscriptionService.cs | Verify same pattern exists here too | CRITICAL |

### Category B: Double-Charge/Double-Refund (Idempotency)
| # | File | Issue | Severity |
|---|------|-------|----------|
| B1 | StripeRefundService.cs | Stripe call + DB read + event publish in single retry block | CRITICAL |
| B2 | StripeRefundService.cs | No fallback idempotency key if caller provides null | CRITICAL |
| B3 | CreateRefundCommand.cs | No state mutation before event publish — concurrent refunds pass validation | CRITICAL |
| B4 | PaymentCompletedConsumer.cs | Check-then-act race — HasCreditForReferenceAsync outside transaction | CRITICAL |
| B5 | LedgerService.CreditSellerAsync | Idempotency check outside transaction boundary | CRITICAL |
| B6 | LedgerService.DebitSellerAsync | Same check-then-act pattern | CRITICAL |
| B7 | RegisterSellerCommand.cs | Check-then-act on SellerProfile existence | HIGH |

### Category C: Ledger Accounting Correctness
| # | File | Issue | Severity |
|---|------|-------|----------|
| C1 | LedgerService.CreditSellerAsync | All 3 entries are Credits (should be Debit on PlatformHolding) | CRITICAL |
| C2 | LedgerService.DebitSellerAsync | All 3 entries are Debits (should be Credit on PlatformHolding) | CRITICAL |
| C3 | RefundIssuedConsumer.cs | Non-deterministic FirstOrDefault picks wrong account (platform vs seller) | CRITICAL |
| C4 | LedgerService.DebitSellerAsync | Guesses account type via SellerPending ?? SellerPayable fallback | HIGH |
| C5 | RefundIssuedConsumer.cs | ReferenceId collision — refund uses same PaymentId as original credit | MEDIUM |

### Category D: Webhook & Concurrency
| # | File | Issue | Severity |
|---|------|-------|----------|
| D1 | StripePaymentProcessor.HandleCompletedSessionAsync | No DbUpdateConcurrencyException catch — xmin guard throws unhandled | HIGH |
| D2 | StripeWebhookProcessor.ProcessEventAsync | Idempotency mark AFTER processing — duplicate window | HIGH |
| D3 | Outbox dual-write assumption | eventPublisher may not share DbContext with repository | HIGH |

### Category E: Batch Processing
| # | File | Issue | Severity |
|---|------|-------|----------|
| E1 | MatureFundsCommand.cs | No FOR UPDATE SKIP LOCKED — multi-instance collision | HIGH |
| E2 | MatureFundsCommand.cs | No OrderBy — non-deterministic batch selection | HIGH |
| E3 | MatureFundsCommand.cs | Missing Distinct() on ownerIds — SQL bloat | MEDIUM |
| E4 | DisbursementService.cs | Verify same patterns | HIGH |

### Category F: Observability & Code Quality
| # | File | Issue | Severity |
|---|------|-------|----------|
| F1 | StripeSubscriptionService.cs | No ILogger — failures invisible | MEDIUM |
| F2 | StripeRefundService.cs | Magic string "succeeded" instead of SDK constants | MEDIUM |
| F3 | Multiple files | Redundant .ConfigureAwait(false) in ASP.NET Core | LOW |

---

## Total: 27 distinct issues
- CRITICAL: 11
- HIGH: 10
- MEDIUM: 5
- LOW: 1

---

## Prevention Plan: How to Stop This From Recurring

### 1. Architecture Guard Tests (Automated CI Enforcement)
Add to `tests/Platform.ArchitecturalGuards/PlatformGuardTests.cs`:

- **No try/catch inside Polly ExecuteAsync**: Scan all files using `_resiliencePolicy.ExecuteAsync` or `policy.ExecuteAsync` and verify no `catch` block exists inside the delegate body.
- **No event publish without SaveChanges**: Scan for `PublishAsync` calls and verify `SaveChangesAsync` follows in the same method.
- **No FirstOrDefaultAsync on multi-entry tables without explicit filter**: Scan LedgerEntries queries for missing `EntryType`/`AccountType` filters.
- **No Take() without OrderBy()**: Scan for `.Take(` not preceded by `.OrderBy(` in the same LINQ chain.
- **All financial commands must have idempotency key parameter**: Scan command records in Payments/Payouts for `IdempotencyKey` property.

### 2. Mandatory Code Review Checklist (For All Financial Code)
Before ANY PR touching Payments/Payouts/Checkout:
- [ ] Is the Polly retry delegate free of exception swallowing?
- [ ] Is the idempotency key mandatory (not optional/nullable)?
- [ ] Is state mutated and saved BEFORE events are published?
- [ ] Are ledger lookups filtered by EntryType AND AccountType?
- [ ] Are batch queries using FOR UPDATE SKIP LOCKED?
- [ ] Is DbUpdateConcurrencyException caught for concurrent operations?
- [ ] Are debit/credit entries balanced (sum debits = sum credits)?

### 3. Integration Test Coverage Requirements
Every financial handler/consumer MUST have tests for:
- Happy path
- Duplicate message delivery (idempotency)
- Concurrent execution (race condition)
- Partial failure (Stripe succeeds, event publish fails)
- Over-refund attempt
- Wrong account lookup

### 4. Codebase-Wide Pattern Scan
Run the same Gemini 12-lens audit against ALL services, not just Payments/Payouts:
- Catalog consumers
- Orders consumers
- Checkout saga handlers
- Notification consumers
- Webhook dispatchers
- Content handlers
- Identity handlers

---

## Execution Plan

### Phase 1: Fix All 11 Critical Issues (Immediate)
Parallel agents in isolated worktrees:
- Agent 1: Fix A1+A2+A3 (Polly swallowed exceptions)
- Agent 2: Fix B1+B2+B3 (Double-refund + idempotency)
- Agent 3: Fix B4+B5+B6 (Check-then-act races in consumers/ledger)
- Agent 4: Fix C1+C2+C3 (Ledger accounting correctness)

### Phase 2: Fix All 10 High Issues
- Agent 5: Fix C4+D1+D2+D3 (Concurrency + deterministic lookup)
- Agent 6: Fix E1+E2+E4+B7 (Batch processing + registration race)

### Phase 3: Add Prevention Guards
- Add 5 new architecture guard tests
- Add integration tests for each fixed handler
- Run 12-lens scan against remaining services

### Phase 4: Verify
- Full build + all guards pass
- Re-run Gemini audit to confirm zero remaining issues
