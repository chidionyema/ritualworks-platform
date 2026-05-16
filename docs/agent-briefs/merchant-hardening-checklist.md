# Merchant Service — Staff-Level Hardening Checklist

## Edge Cases (Day 0 Requirements)

### Concurrency & Data Integrity
- [ ] xmin concurrency token on MerchantProfile (prevents lost updates)
- [ ] Unique constraint on OwnerId (one merchant per owner)
- [ ] Unique constraint on Slug (with retry on conflict)
- [ ] Idempotent create: if OwnerId already has merchant, return 409 Conflict with existing ID
- [ ] DbUpdateConcurrencyException handling in all write commands

### State Machine Guards
- [ ] Domain guards enforce valid transitions only:
  - Pending → Active (approve), Rejected (reject)
  - Active → Suspended (suspend), Deactivated (deactivate)
  - Suspended → Active (activate), Deactivated (deactivate)
  - Rejected → Pending (re-apply only, not direct to Active)
  - Deactivated → terminal (no transitions out)
- [ ] InvalidOperationException on illegal transitions (with descriptive message)
- [ ] Unit tests for every valid AND invalid transition

### Audit & Accountability
- [ ] RejectionReason stored on entity (not just event)
- [ ] SuspensionReason stored on entity
- [ ] ApprovedAt, RejectedAt, SuspendedAt, DeactivatedAt timestamps
- [ ] ApprovedBy, RejectedBy, SuspendedBy admin user IDs
- [ ] All state changes publish domain events with actor + timestamp

### Operating Hours
- [ ] Timezone field on MerchantProfile (IANA string, e.g., "America/New_York")
- [ ] Validate timezone with TimeZoneInfo.FindSystemTimeZoneById
- [ ] Validate OpenTime < CloseTime (unless 24h — both 00:00)
- [ ] Max 7 entries (one per day), enforce uniqueness on (MerchantId, DayOfWeek)
- [ ] Default: all days closed until explicitly set

### Security & Input Validation
- [ ] IDOR: All non-admin endpoints verify OwnerId matches authenticated user
- [ ] Slug immutability: UpdateMerchantCommand cannot modify slug
- [ ] HTML sanitization on Bio, Description (strip tags or reject)
- [ ] URL validation on LogoUrl, Website (must be https:// or relative)
- [ ] Email format validation on ContactEmail
- [ ] Phone format validation (E.164 or local format)
- [ ] Max field lengths enforced in DB AND validator

### Soft Delete & Visibility
- [ ] Deactivated merchants excluded from public listing (ListMerchants filters by default)
- [ ] GetBySlug/GetById still returns deactivated (for admin/owner viewing)
- [ ] Products remain in DB but are delisted (event: MerchantDeactivatedEvent → Catalog consumer)

### Event-Driven Integration
- [ ] MerchantCreatedEvent → published on successful create
- [ ] MerchantActivatedEvent → Catalog re-lists products
- [ ] MerchantSuspendedEvent → Catalog delists, Checkout blocks new orders
- [ ] MerchantDeactivatedEvent → Catalog delists permanently
- [ ] All events via MassTransit outbox (transactional with DB write)

### Performance
- [ ] ListMerchants: index on (Status, Name) for filtered+sorted queries
- [ ] GetBySlug: index on Slug (already unique)
- [ ] GetByOwner: index on OwnerId (already unique)
- [ ] Pagination bounded: take ∈ [1, 100]

### Testing
- [ ] Unit: All state transitions (valid + invalid)
- [ ] Unit: Validator rejects bad input (empty name, invalid slug, bad email, etc.)
- [ ] Unit: IDOR check (handler rejects mismatched OwnerId)
- [ ] Unit: Idempotent create returns conflict
- [ ] Integration: Slug uniqueness race (concurrent creates)
- [ ] Integration: Concurrency token (concurrent updates)
- [ ] Architecture: No forbidden cross-service dependencies
