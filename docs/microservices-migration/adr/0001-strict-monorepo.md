# ADR-0001: Strict Monorepo with Physically-Enforced Service Boundaries

**Status:** Accepted
**Date:** 2026-05-02
**Deciders:** chidionyema

## Context

We are migrating a .NET 9 monolith with 5 bounded contexts into 7 microservices. The repo strategy choice (polyrepo per service vs single monorepo vs hybrid contracts-monorepo) is the foundational decision — it shapes CI design, code-review workflow, dependency management, and reviewer perception.

Constraint: this is a **portfolio project** for a single engineer-architect. Optimization function: maximum architectural visibility per hour spent, not "minimum risk for a 50-engineer org." Recruiters and consulting clients will look at *one* repo or zero — they will not click through 7 GitHub repos.

## Decision

**Single monorepo with strictly enforced physical service boundaries.** Each service lives in its own `src/<Service>/` folder with its own `.sln`, its own DbContext, its own NuGet dependencies, and **zero project references to sibling services**. Communication between services is **only** via:

1. `Haworks.Contracts` NuGet package (shared event records + .proto definitions).
2. `Haworks.BuildingBlocks` NuGet package (cross-cutting infrastructure: outbox, MediatR behaviors, OTel registration, Vault interceptor).
3. RabbitMQ events (async).
4. gRPC calls (sync).

The build fails if any service references another service's project. Three enforcement layers:
- `Directory.Build.props` per service folder blocks cross-service `ProjectReference`.
- NetArchTest assertion in each service's Architecture test project.
- Custom CI grep step for `using Haworks.<OtherService>` namespace references.

## Options Considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Strict monorepo (chosen)** | One repo for recruiters; shared CI templates; trivial cross-service refactors during early development; one Renovate config. | Easy to accidentally re-merge services unless boundaries are enforced; CI must be path-aware to avoid running everything on every PR. | **Chosen.** Enforcement layers eliminate the "monolith pretending" failure mode. |
| Polyrepo (1 repo per service) | True independent deployability; standard enterprise pattern; clean CI per service. | 7+ repos to maintain; 7+ Renovate configs; 7+ CODEOWNERS files; cross-service refactors are multi-PR dances; recruiters won't click through 7 repos. | Rejected. The social cost is paid for an audience (large teams) we don't have. |
| Hybrid (contracts repo + services monorepo + per-service deploy manifests) | Contracts independently versioned for external consumers; services share tooling. | Adds repo complexity without solving the recruiter-discoverability problem; 2 repos is still 2 GitHub URLs to share. | Rejected for portfolio scope. Would adopt for a real enterprise team with external API consumers. |

## Consequences

### Positive
- One impressive repo with one killer README is the entire portfolio surface.
- Cross-service refactors during the migration (especially Phase 0–3) are atomic PRs.
- Shared CI workflow + `dorny/paths-filter` means each service's CI is fast (only affected jobs run).
- `Haworks.Testing.Containers` NuGet shared by all services prevents test-infra drift.
- One Renovate config covers the whole tree.

### Negative
- The boundary enforcement is the load-bearing piece. If any of the three layers (Directory.Build.props, NetArchTest, CI grep) silently breaks, the monorepo regenerates a monolith over weeks. **Mitigation:** integration test that *attempts* a forbidden cross-service reference and asserts the build fails.
- Path-filtered CI is more complex to reason about than per-repo CI. **Mitigation:** explicit comment in `ci.yml` mapping each path filter to its triggered jobs.
- A single git push that touches multiple services triggers multiple service builds — slower individual PRs vs polyrepo. **Mitigation:** acceptable at this scale; revisit if PR runtime exceeds 15 min.

### Neutral
- Contributors (eventually) need to understand the boundary rules. **Mitigation:** `CONTRIBUTING.md` with one section per rule + a `failing-boundary-test-example` branch demonstrating what a violation looks like.

## Notes

If this project ever needs to be scaled to a large team, the migration to polyrepo is one PR per service: `git filter-repo --subdirectory-filter src/<Service>/`. The strict boundary enforcement makes this trivial.

Reference: [01-architecture.md § Monorepo Layout & Boundary Enforcement](../01-architecture.md#monorepo-layout--boundary-enforcement)
