# RitualWorks Platform — Engineering Standards & Context

This document defines the foundation rules for the RitualWorks microservices platform.

## 🚀 Quality & Validation Mandates

### 1. Mandatory MediatR Validation
- **Rule:** EVERY MediatR `Command` or `Query` MUST have a corresponding `AbstractValidator<T>` implementation in the same assembly.
- **Enforcement:** Validation is wired into the pipeline via `ValidationBehavior`. Bypassing this behavior by manually validating in controllers or handlers is FORBIDDEN.
- **Standard:** Use `FluentValidation.TestHelper` to ensure 100% coverage of validator rules.

### 2. Result Pattern Consistency
- **Mandate:** Controllers MUST NOT contain business logic. They act as thin wrappers around MediatR.
- **Standard:** Every handler returns `Result<T>`. Use `.ToActionResult()` extension to map results to appropriate HTTP status codes (400 for Validation, 404 for NotFound, etc.).

## 🏛 Native Architecture

### 1. Bounded Context Isolation
- **Rule:** Cross-context communication is exclusively via **Events** (MassTransit) or **BFF-Web** orchestration. Direct DB access or HTTP calls between services are prohibited.
- **Data:** References between contexts must be ID-based (Guid), never navigation properties.

### 2. Transactional Integrity (Outbox)
- **Rule:** All side effects (event publishing) MUST use the **Outbox Pattern**. Commit domain state and outbox messages in a single atomic transaction.

### 3. Geospatial Integrity (Location Service)
- **Rule:** All coordinates MUST be stored as `GEOGRAPHY(POINT, 4326)` in PostgreSQL.
- **Geohashing:** Every location record MUST have a corresponding 12-char Geohash for efficient grid-based indexing.
- **Search:** Proximity searches MUST be performed against the Elasticsearch `geo_point` projection, followed by gRPC hydration for rich metadata.

## 🧠 Institutional Memory

### The "Validation Gap" Remediation (May 2026)
- Found critical gaps where `CheckoutOrchestrator` and other services lacked validation.
- **Action:** Implementing `ValidationBehavior` across all services. Every PR adding a Command MUST include a Validator and ValidatorTests.
