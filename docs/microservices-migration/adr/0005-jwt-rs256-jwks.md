# ADR-0005: JWT Switches to RS256 with JWKS Endpoint

**Status:** Accepted
**Date:** 2026-05-02
**Deciders:** chidionyema

## Context

The monolith signs JWTs with HS256 — a symmetric algorithm using a shared secret stored in Vault at `secret/jwt:Key`. Today this works because there's one process and one secret consumer.

Post-migration, **every service** validates JWTs (every protected endpoint, every gRPC call). With HS256, every service needs the shared secret in its environment. Rotating that secret requires a coordinated restart of N services with both old and new secrets accepted during the rollover window — exactly the kind of operational headache that catches teams out.

## Decision

**Switch JWT signing from HS256 (symmetric) to RS256 (asymmetric).**

- identity-svc generates an RSA keypair, stores the **private** key in Vault `secret/identity/jwt-signing`, and signs all JWTs with it.
- identity-svc exposes `/.well-known/jwks.json` — the standard JWKS endpoint — serving the **public** key for validation.
- Every other service validates JWTs by fetching JWKS once (cached with TTL), refreshing on key rotation. **No shared secret to distribute.**
- Old HS256 tokens accepted via dual-validation for a 7-day deprecation window during Phase 1 cutover.

For the rare cross-service shared HMAC secret that must stay symmetric (e.g., `HubSecurity:SubscriptionTokenSecret`), version it in Vault (`secret/shared/hub:v2`) and accept N and N-1 during rollover.

## Options Considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| Stay on HS256 | Zero migration work. | Every service holds the shared secret. Rotation requires coordinated multi-service restart. Secret in env var visible in every pod's spec. | Rejected. The exact problem we should solve before extracting more services. |
| **Switch to RS256 + JWKS (chosen)** | No shared secret across services. Rotation = identity-svc generates new key, publishes new JWKS, old key honored for deprecation window. Standard pattern (OAuth2/OIDC). | Slightly larger tokens (~3x). One-time migration cost. | **Chosen.** Eliminates the worst secret-rotation problem post-split. |
| ES256 (elliptic curve, also asymmetric) | Smaller tokens than RS256. Same JWKS pattern. | Less universal SDK support; more careful curve selection needed. | Considered, would adopt for a real production system; **deferred** as portfolio doesn't need the optimization. |
| OIDC provider (Auth0, Cognito) | No identity code to maintain. | Out-of-scope dependency for a portfolio piece — we want to *show* the auth code. | Rejected for portfolio purposes. |

## Consequences

### Positive
- Zero shared-secret distribution across services. **Single biggest secret-management win of the migration.**
- Standard JWKS pattern recognizable to every reviewer.
- identity-svc remains the sole authority for token issuance; everyone else is a stateless validator.
- Key rotation is non-coordinated: identity-svc rotates whenever; consumers refresh JWKS on cache miss.

### Negative
- One-time migration: every JWT-validating code path must support both algorithms during the 7-day Phase 1 cutover. **Mitigation:** dual-validation middleware in `Haworks.BuildingBlocks.Auth` removes the per-service code burden.
- JWKS endpoint must be highly available — if identity-svc is down, no service can validate new tokens. **Mitigation:** JWKS cache with long TTL (e.g., 1 hour) means an identity-svc outage doesn't immediately break validation. Aggressive cache + circuit breaker on the JWKS fetch.
- Slightly larger tokens (RSA signature ~256 bytes vs HMAC ~32 bytes). **Acceptable.**

### Neutral
- JWKS cache must be invalidated proactively on rotation. **Mitigation:** identity-svc publishes `JwtSigningKeyRotated` event; consumers invalidate cache + refresh.

## Notes

Implementation outline for the migration:

1. **Phase 0:** Add RS256 support alongside HS256 in `Haworks.BuildingBlocks.Auth`. Both algorithms accepted by validators.
2. **Phase 1:** identity-svc generates RSA keypair → publishes JWKS → starts signing new tokens with RS256. Old HS256 tokens still validated by all services (dual-validation).
3. **Phase 1 + 7 days:** identity-svc stops signing HS256. Existing HS256 tokens expire naturally (max 60 min for access tokens).
4. **Phase 2 onward:** HS256 validation code removed.

Reference: [02-platform.md § Authentication & JWT](../02-platform.md#authentication--jwt)
