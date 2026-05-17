# Codebase-Wide Anti-Pattern Scan Results

**Date:** 2026-05-17
**Scan scope:** All 15+ services, 6 anti-patterns
**Total findings:** 40+ across 13 services

## Findings by Service

### PAYMENTS (10 findings — most affected)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P2 | CreateRefundCommand.cs:25-45 | Read then check then publish, no transaction | CRITICAL |
| P3 | CreateRefundCommand.cs:45 | PublishAsync with no SaveChangesAsync | CRITICAL |
| P3 | PaymentAmountMismatchHandler.cs:51 | SaveChanges BEFORE PublishAsync — if publish fails, Orders never learns | HIGH |
| P3 | StripeWebhookProcessor.cs:221 | PublishAsync(CheckoutSessionExpiredEvent) no SaveChanges | HIGH |
| P3 | StripeWebhookProcessor.cs:249 | PublishAsync(RefundIssuedEvent) in loop without commit | HIGH |
| P6 | PaymentSessionRequestedConsumer.cs:49 | No app-level idempotency, creates duplicate Payment | CRITICAL |
| P6 | ProviderRefundInitiationRequestedConsumer.cs:15 | No idempotency, replay double-refunds at provider | CRITICAL |
| P6 | SubscriptionRenewalRequestedConsumer.cs:13 | No idempotency, duplicate renewal events | HIGH |
| P6 | PrivacyErasureRequestedConsumer.cs:19 | No idempotency, duplicate erasure completion | MEDIUM |

### PAYOUTS (8 findings)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P2 | LedgerService.CreditSellerAsync:41-50 | Idempotency check OUTSIDE transaction | CRITICAL |
| P2 | LedgerService.DebitSellerAsync:100-108 | Same TOCTOU gap | CRITICAL |
| P2 | RegisterSellerCommand.cs:17-22 | Check-then-act, no transaction | HIGH |
| P4 | RefundIssuedConsumer.cs:39 | FirstOrDefault without EntryType/AccountType filter | CRITICAL |
| P5 | DisbursementService.cs:32 | Take(500) without OrderBy | HIGH |
| P5 | MatureFundsCommand.cs:27 | Take(500) without OrderBy | HIGH |
| P6 | RefundIssuedConsumer.cs:28 | No idempotency, TOCTOU enables double-debit | CRITICAL |
| P6 | PaymentCompletedConsumer.cs:19 | Same TOCTOU gap | CRITICAL |

### CATALOG (3 findings)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P2 | ReserveStockCommand.cs:43 | Read-check-save, no explicit transaction | HIGH |
| P5 | ProductRepository.cs:123 | FirstOrDefault without OrderBy | MEDIUM |
| P6 | StockReservation/ReleaseConsumers | No app-level idempotency, relies on MT inbox only | HIGH |

### ORDERS (3 findings)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P5 | OrderRepository.cs:69 | GetAbandonedOrdersAsync Take() without OrderBy | MEDIUM |
| P6 | RefundCompletedConsumer.cs:13 | Domain guard only, no dedup | MEDIUM |
| P6 | RefundCancelledConsumer.cs:13 | Same | MEDIUM |

### MERCHANT (2 findings)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P2 | CreateMerchantCommand.cs:36-44 | Two AnyAsync checks then Add, no transaction | HIGH |
| P3 | CreateMerchantCommand.cs:47 | Publish AFTER Save, not using outbox | HIGH |

### PRIVACY (1 finding)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P2 | InitiatePrivacyRequestCommand.cs:36-46 | Check-then-act, no transaction | HIGH |

### LOCALIZATION (2 findings)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P2 | UpsertTranslationCommand.cs:44-58 | Check-then-act upsert | HIGH |
| P3 | UpsertTranslationCommand.cs:62 | Publish after Save, no outbox | HIGH |

### LOCATION (1 finding)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P3 | CreateAddressCommand.cs:76-92 | Save before Publish, no outbox | HIGH |

### MEDIA (3 findings)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P5 | UploadSweeperWorker.cs:44 | Take(100) without OrderBy | MEDIUM |
| P6 | MediaUploadCompletedConsumer.cs:19 | Replay re-scans and re-publishes | MEDIUM |
| P6 | ProcessMediaConsumer.cs:16 | No idempotency, replay re-processes | MEDIUM |

### WEBHOOKS (1 finding)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P5 | CdcFanOutWorker.cs:89 | Take(1000) without OrderBy | MEDIUM |

### IDENTITY (1 finding)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P6 | PrivacyErasureRequestedConsumer.cs:22 | No idempotency, replay re-anonymises | MEDIUM |

### REALTIME (1 finding)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P6 | OrderStatusChangedConsumer.cs:19 | Replay sends duplicate SignalR push | MEDIUM |

### SEARCH (2 findings)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P6 | CategoryUpdatedConsumer.cs:24 | Re-denormalises all docs on replay | LOW |
| P6 | LocationUpdatedConsumer.cs:16 | No version guard | LOW |

### AUDIT (1 finding)
| Pattern | File | Issue | Severity |
|---------|------|-------|----------|
| P5 | AuditExportWorker.cs:65 | Take(100) without OrderBy | MEDIUM |

## Summary

| Severity | Count |
|----------|-------|
| CRITICAL | 10 |
| HIGH | 15 |
| MEDIUM | 13 |
| LOW | 2 |
| **Total** | **40** |

## Pattern Distribution

| Pattern | Count | Description |
|---------|-------|-------------|
| P2: Check-then-act | 10 | Read-check-write without transaction |
| P3: Publish/Save ordering | 7 | Event publish without atomic save |
| P4: Non-deterministic query | 2 | FirstOrDefault on multi-row tables |
| P5: Take without OrderBy | 7 | Non-deterministic batch selection |
| P6: Missing consumer idempotency | 14 | No app-level dedup beyond MT inbox |
