Audit a service against the 12 architectural lenses from CLAUDE.md.

Usage: /audit-service Payments

Read the service's Domain, Application, Infrastructure, and Api layers. Check for:
1. Data & Domain Integrity — bounds checking, negative balance prevention
2. Concurrency — pessimistic locks, FOR UPDATE usage
3. Transaction Boundaries — outbox patterns, rollback compensations
4. State Machine — forced/invalid transitions
5. Idempotency — safe retries for APIs and webhooks
6. Error Handling — swallowed exceptions, orphaned data
7. Zero-Trust Security — rate limiting, JWT validation, IDOR prevention
8. Integration Boundaries — sanitization
9. Scalability — unbounded queries, N+1
10. Testing — missing failure paths
11. Configuration — hardcoded secrets
12. Database — schema constraints, foreign keys

Report findings by severity (CRITICAL/HIGH/MEDIUM/LOW).
