# 06 — QA-Hardened Migration Strategy

This document outlines the "Clean Cut" migration strategy, incorporating the Principal QA findings from the monolithic stabilization phase.

## 🏁 The "Clean Cut" Principle

We bypass the "Multi-Outbox Monolith" phase. Running multiple outbox delivery services in a single process leads to MassTransit singleton collisions and `NullReferenceException` (C12).

### Strategic Bridge
1. **Monolith:** Keep the consolidated outbox (OrderDbContext) for E2E stability.
2. **Extraction:** Port each service to a fresh process. Each process registers exactly **one** outbox.
3. **Seams:** Use the E2E suite to verify that the "network hop" between the Monolith and the new service is transparent to the user.

## 🛡️ Hardened Security Standards

Every extracted service must adhere to these non-negotiable security patterns:

1.  **CSRF Invariant:** The BFF (`bff-web`) must enforce `AutoValidateAntiforgeryToken` globally. Backends trust the BFF via mTLS and scoped JWTs.
2.  **Token Revocation:** Every service that performs an authority check must verify the `jti` against a distributed revocation list (L1/L2 cache).
3.  **Deterministic Idempotency:** Hashing of `UserId` + `ClientKey` using SHA-256 must be standard in all mutating handlers.
4.  **Vault Bootstrapping:** Services must pull secrets into `IConfiguration` BEFORE starting the DI container.

## 🧪 Testing Pyramid (Distributed Version)

| Level | Tool | Focus |
| :--- | :--- | :--- |
| **E2E** | Playwright | Full user journey via BFF. Includes SignalR verification. |
| **Smoke** | Aspire.Testing | Live environment connectivity (Stripe/PayPal Sandbox). |
| **Contract** | PactNet v5 | Message Pacts for all cross-service integration events. |
| **Chaos** | Testcontainers | Resiliency verification (Pausing RabbitMQ/Vault mid-flow). |
| **Unit** | xUnit/Moq | 70% coverage on Handlers and Domain invariants. |

## 🚀 Key Implementation Fixes (Ported from Monolith)

- **Type Preservation:** All publishers must use `(object)` casting to prevent type erasure in RabbitMQ.
- **Resilient Scheduling:** Sagas must use `.If(ctx => ctx.TryGetPayload<MessageSchedulerContext>(out _), ...)` to support environments without the RabbitMQ delayed message plugin.
- **JWT Robustness:** Implementation must include UTF-8 fallback for plain-text secrets in developer environments.

## ⏱ Migration Sequence (The Hedges)

1. **Hedge 1 (Contracts):** Port all `Domain/Events` to `Haworks.Contracts` immediately to lock the cross-service schema.
2. **Hedge 2 (BuildingBlocks):** Centralize `Result<T>`, `Error`, and `DynamicCredentialsInterceptor`.
3. **Hedge 3 (BFF Edge):** Scaffold the BFF early. Moving SignalR and Cookies is the hardest task; do it first while the backend is still monolithic.
