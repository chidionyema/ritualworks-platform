# Service Audit Protocol — 12-Lens Deep Review

> Standard operating procedure for auditing any service in the haworks-platform.
> Used by Claude Code, Gemini CLI agents, and human reviewers.

## When to Use

- Before a service goes to production
- After major refactors
- During periodic quality sweeps (wave protocol)
- When investigating a production incident root cause

## Phase 1: Complete Read (NO SKIMMING)

Read EVERY `.cs` file in the target service — Domain, Application, Infrastructure, API, and Tests.

Build a mental model of:
- Entity relationships and invariants
- Command/query flow through MediatR pipeline
- Consumer message flow (MassTransit)
- State machines and transitions
- Database schema and migrations
- Test coverage map (what IS tested vs what ISN'T)

**Do NOT skip files.** Do NOT skim. A skimmed audit misses the bugs that matter.

For Gemini agents: use `find src/{Service} -name '*.cs' | sort` and read each file in order. Store findings as you go — don't wait until the end.

## Phase 2: Multi-Lens Systematic Audit

Review against ALL 12 categories simultaneously. For each finding, record:
- File path + line number
- Severity: Critical / High / Medium / Low
- Category (lens number)
- Root cause description
- Recommended fix

---

### Lens 1: Money Correctness
| Check | What to look for |
|-------|-----------------|
| Double-charge | Can a payment be processed twice? |
| Double-refund | Can a refund be issued twice for the same payment? |
| Ledger balancing | Do debits always equal credits? |
| Decimal precision | `decimal` only (never `float`/`double`). Explicit rounding (`MidpointRounding.ToEven`). |
| Amount comparison | Never `==` on money — use tolerance or exact decimal match |
| Currency tracking | Is currency stored? Can mixed-currency operations silently occur? |

### Lens 2: Concurrency
| Check | What to look for |
|-------|-----------------|
| Race conditions | Read-then-write without `RowVersion`/`xmin` optimistic concurrency |
| TOCTOU | Time-of-check-to-time-of-use gaps (check stock → reserve stock) |
| Saga conflicts | Concurrent saga instances for the same entity |
| Worker overlap | Can two hosted service instances run simultaneously? |
| Lock contention | Distributed locks missing or overly broad |

### Lens 3: Transaction Boundaries
| Check | What to look for |
|-------|-----------------|
| Outbox atomicity | Events published inside the same DB transaction as state changes? |
| Events outside tx | Any `Publish()` outside `SaveChangesAsync()`? |
| Partial commits | Failure leaves DB + broker inconsistent? |
| Implicit 2PC | Distributed transaction that should be a saga? |

### Lens 4: State Machine Correctness
| Check | What to look for |
|-------|-----------------|
| Invalid transitions | Can the state machine reach an illegal state? |
| Missing compensation | Every forward step has a rollback path? |
| Stuck states | Timeout/sweep for states that never transition? |
| Unhandled events | Event arrives in wrong state — what happens? |
| Terminal re-entry | Can completed/failed saga be restarted? |

### Lens 5: Idempotency
| Check | What to look for |
|-------|-----------------|
| Provider calls | Payment/external API calls use idempotency keys? |
| Webhook handlers | Same webhook fires twice without side effects? |
| Consumer retry | MassTransit retry — consumer handles duplicates? |
| Outbox replay | Consumers deduplicate replayed outbox messages? |

### Lens 6: Error Handling
| Check | What to look for |
|-------|-----------------|
| Swallowed exceptions | `catch { }` without logging? |
| Missing rollback | Failure leaves partial state changes? |
| Leaked state | Caught exception leaves entity in intermediate state? |
| Infinite retry | Retry policies bounded? Dead-letter queue exists? |
| Error leakage | 500 responses expose stack traces? |

### Lens 7: Security
| Check | What to look for |
|-------|-----------------|
| IDOR | Every query filters by authenticated identity from JWT |
| Auth gaps | Every state-changing endpoint has `[Authorize]` |
| Input validation | All external inputs validated (FluentValidation) |
| PII in logs | Sensitive fields redacted before logging |
| Injection | SQL via string interpolation? Expression injection? |
| SSRF | User-supplied URLs validated before HTTP calls? |

### Lens 8: Integration Correctness
| Check | What to look for |
|-------|-----------------|
| Consumer contracts | Consumer handles all fields producer sends? |
| Null fields | Optional event field null — handled gracefully? |
| Event ordering | Consumer assumes ordering not guaranteed? |
| Saga coordination | Events published in right order with correlation IDs? |
| Dead events | Published events with no consumer? |

### Lens 9: Scalability
| Check | What to look for |
|-------|-----------------|
| Memory bombs | Unbounded `ToListAsync()` without pagination |
| Blocking calls | `.Result` or `.Wait()` on async paths |
| N+1 queries | Navigation property in loops without `.Include()` |
| Unbounded responses | Can a response return millions of records? |
| Large payloads | File uploads streamed or loaded into memory? |

### Lens 10: Testing
| Check | What to look for |
|-------|-----------------|
| Happy path | Is the primary flow tested end-to-end? |
| Failure paths | Every compensation/error path tested? |
| Saga tests | Compensation and stuck-state scenarios? |
| Concurrency tests | Race conditions tested (parallel requests)? |
| Test isolation | Fresh DB per test? No shared mutable state? |
| Negative tests | What inputs should fail but aren't tested? |

### Lens 11: Configuration
| Check | What to look for |
|-------|-----------------|
| Missing options | External URLs, timeouts, feature flags configurable? |
| Hardcoded values | Magic numbers, inline URLs, hardcoded connection strings? |
| Env assumptions | Code that only works in dev/prod but not both? |
| Options validation | `ValidateDataAnnotations().ValidateOnStart()`? |
| Secrets exposure | Secrets in appsettings.json instead of Vault/env vars? |

### Lens 12: EF/Database
| Check | What to look for |
|-------|-----------------|
| Schema match | Migration matches entity configuration? |
| Migration gaps | Entity changes without corresponding migrations? |
| Outbox tables | MassTransit inbox/outbox configured? |
| Indexes | Query-heavy columns indexed? Unique constraints present? |
| Raw SQL | Schema-prefixed? PascalCase tables? `xmin` for concurrency? |
| Connection scope | `DbContext` scoped correctly? No ambient transactions? |

---

## Phase 3: Findings Report

Output format for each finding:
```
### [SEVERITY] Lens #N — Category Name
**File:** `src/{Service}/{Layer}/path/to/File.cs:42`
**Issue:** <one-line description>
**Impact:** <what can go wrong>
**Fix:** <specific code change or pattern to apply>
```

Group findings by severity (Critical first), then by lens number.

## Phase 4: Fix & Guard

For every Critical/High finding:
1. **Fix** the code in the target service
2. **Guard** — add an architecture guard test in `tests/Platform.ArchitecturalGuards/PlatformGuardTests.cs`
3. **Document** — update the Root Causes Catalogue if a new pattern is discovered
4. **Verify** — `dotnet test tests/Platform.ArchitecturalGuards/` passes

## Platform-Specific Rules (MUST CHECK)

These are patterns that have caused real bugs in this codebase:

| Rule | Why |
|------|-----|
| `{ get; init; }` on all MassTransit events, never positional records | `Init<T>` faults on positional records |
| Schema-prefix ALL raw SQL | EF uses `HasDefaultSchema()` — unqualified SQL hits wrong schema |
| `SELECT *, xmin` not `SELECT *` | `*` excludes system columns needed for optimistic concurrency |
| `ConfigureTestServices` not `ConfigureServices` | Test overrides must run AFTER app DI |
| Never `EnsureDeletedAsync` in tests | Drops the entire database |
| Never `$""` with `ExecuteSqlRawAsync` | EF treats `{var}` as parameters |
| Use `Result<T>` for expected failures | No raw exceptions in application layer |
| Every controller with state changes needs `[Authorize]` | Auth + user identity from JWT claims only |
