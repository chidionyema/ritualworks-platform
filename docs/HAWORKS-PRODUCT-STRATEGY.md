# Haworks: Product Strategy & Packaging Roadmap

> **Status:** Strategic vision (2026). CLI tooling and NuGet SDK packaging are not yet implemented. See [docs/BACKLOG.md](BACKLOG.md) for current priorities.

This document outlines the strategic vision for transforming the Haworks platform from a reference architecture into a highly distributable, enterprise-grade product.

---

## 1. Developer Experience: The Haworks CLI
To simplify the management of a complex microservices ecosystem, we will develop a native CLI tool.
*   **Command Suite:**
    *   `haworks init`: Bootstrap a new platform instance with pre-configured infrastructure (Vault, Postgres, RabbitMQ).
    *   `haworks add-service <template>`: Generate a new service that automatically inherits the `BuildingBlocks` (MediatR, Outbox, OTel).
    *   `haworks run <profile>`: Launch specific service groups (e.g., `financial`, `marketing`, `core`) using Aspire.
*   **Goal:** Reduce "Time-to-First-Hello-World" for new developers from hours to minutes.

## 2. Distribution: Modular NuGet SDKs
We will decouple the `BuildingBlocks` into versioned, published NuGet packages to support external extensions.
*   **Proposed Packages:**
    *   `Haworks.Common`: Result patterns, Error types, and standard extensions.
    *   `Haworks.Messaging`: MassTransit abstractions, Inbox/Outbox configuration.
    *   `Haworks.Telemetry`: Standardized OpenTelemetry wire-up.
    *   `Haworks.Persistence`: Postgres/Entity Framework base classes and spatial helpers.
*   **Benefit:** Allows third-party teams to build services that are 100% compatible with the Haworks event bus and observability suite without having the full platform source code.

## 3. Operational Scalability: White-Label Helm Charts
Packaging the entire platform as a single, configurable Helm Chart.
*   **Capabilities:**
    *   Dynamic enabling/disabling of services via `values.yaml`.
    *   Native integration with External Secrets Operator (replacing/supplementing Vault).
    *   Auto-scaling rules based on the custom OTel metrics we've established.
*   **Goal:** Provide a "One-Click Deploy" experience for enterprise clients on AWS (EKS) and Azure (AKS).

## 4. Business Model: Native Multi-Tenant SaaS
Transforming the architecture to support safe data partitioning.
*   **Mechanism:** Inject `TenantId` into every MediatR command and enforce it via EF Core Global Query Filters.
*   **Messaging:** Include `TenantId` in all event headers to ensure cross-service logic (like Notifications or Analytics) remains strictly partitioned.
*   **Value:** Enables the platform to be sold as a hosted SaaS solution with high margin due to shared infrastructure costs.
