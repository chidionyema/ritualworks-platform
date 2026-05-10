# Platform Reusability — End-to-End Spec

**Status:** vision spec. Will spawn phase-specific implementation specs.

## 1. Goal & non-goals

### Goal

Extract everything that's reusable from this platform — the wave protocol, the cross-cutting services, the agent rules, the deploy patterns, the test pyramid — into a versioned `wave-platform-blueprint` repo that any new project can consume to inherit the same way of working.

The promise: **any new project — a different e-commerce, a healthcare platform, a SaaS dashboard — starts with the same disciplined operating model that took months to build here**. They get parallel-agent waves, day-0 deploy, the cross-cutting services pre-built, the architecture checks, the testing rigour, the agent rules — all from `npx wave-platform init`.

The same blueprint supports multiple stacks (.NET, React, Python, Go, Swift, etc.) — different scaffold templates, same protocol.

### Non-goals

- A "framework" that locks projects in. The blueprint is composable — projects use what they need, override what they don't.
- A code-generator that outputs a finished application. The blueprint is the **operating model**, not the application; agents fill in the application via `wave run`.
- A monorepo for every project. Each project keeps its own repo. The blueprint is a dependency.

## 2. Architecture

Three layers. Each layer is independently versioned + consumed.

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Layer A — Wave Protocol (stack-agnostic)                                │
│ - wave CLI (bash, no runtime dependencies beyond git/gh)                │
│ - SUPERPROMPT.md / WAVE.md / brief format / mode contract               │
│ - Stress-tested race-safe claim + sentinel + zombie reclaim             │
│ - Architecture check (per-stack rule files plug in)                     │
└──────────────────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────────────────┐
│ Layer B — Per-Stack Adapters (folders, one per stack)                   │
│ - stacks/dotnet/   — 4-project DDD scaffold, Aspire wiring, NuGet refs  │
│ - stacks/react/    — Vite + React + Tailwind + Storybook scaffold       │
│ - stacks/react-native/  — Expo + RN scaffold, native modules            │
│ - stacks/fastapi/  — Python FastAPI + Pydantic + SQLModel               │
│ - stacks/go/       — Go modules + clean-architecture layout             │
│ - stacks/swift/    — SwiftUI iOS scaffold                               │
│ - stacks/kotlin/   — Spring Boot or Ktor scaffold                       │
│ Each stack folder ships:                                                │
│   templates/      scaffold templates with {{FEATURE}} placeholders      │
│   rules/          stack-specific agent rules                            │
│   architecture/   per-stack architecture-check rules                    │
│   tests/          test-pyramid adapters (E2E harness, fixture pattern)  │
│   deploy/         per-stack deploy templates (Dockerfile, fly.toml,    │
│                   helm chart, k8s manifests)                            │
└──────────────────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────────────────┐
│ Layer C — Cross-Cutting Services (deployable products)                  │
│ - audit-svc       — published OCI image + Haworks.Audit.Client NuGet   │
│ - notifications-svc — same pattern                                       │
│ - payments-svc                                                           │
│ - search-svc                                                             │
│ - content-svc                                                            │
│ - identity-svc    — auth-as-a-service                                    │
│ - cdc-svc         — logical-replication relay                            │
│ - webhooks-svc                                                           │
│ - feature-flags-svc                                                      │
│ Each ships as:                                                           │
│   - OCI image at ghcr.io/wave-platform/<svc>:<version>                  │
│   - Helm chart for in-cluster deployment                                 │
│   - Client SDK per stack (.NET NuGet, npm, PyPI, Go module)             │
│   - Spec doc describing the contract                                     │
└──────────────────────────────────────────────────────────────────────────┘
```

A new project consumes whichever layers it needs:
- Want the protocol but write your own services? Layer A only.
- Standard .NET project? Layer A + Layer B's `dotnet` + whichever Layer C services apply.
- Healthcare platform on Python? Layer A + Layer B's `fastapi` + Layer C's `audit`/`identity`/`webhooks` (skip the e-commerce-shaped ones).

## 3. The blueprint repo structure

`wave-platform-blueprint/` — its own GitHub repo, separate from any individual project.

```
wave-platform-blueprint/
├── README.md                           # what this is + getting started
├── tools/
│   ├── wave-platform                   # the bootstrap CLI: 'wave-platform init <name>'
│   ├── wave                            # the wave protocol CLI (lifted from this repo)
│   └── architecture-check              # the polyglot architecture checker
├── docs/
│   ├── PROTOCOL.md                     # the SUPERPROMPT.md Mode B contract
│   ├── WAVE.md                         # the operator guide
│   ├── reusability.md                  # this spec
│   ├── adding-a-stack.md               # how to add support for a new stack
│   └── architecture/                   # universal architecture references
│       ├── coupling-rubric.md
│       ├── cross-cutting-services.md
│       └── testing-pyramid.md
├── stacks/
│   ├── dotnet/                         # one folder per stack — see § 4
│   ├── react/
│   ├── fastapi/
│   ├── go/
│   ├── swift/
│   ├── kotlin/
│   └── react-native/
├── services/                           # Layer C — the cross-cutting services
│   ├── audit/
│   │   ├── chart/                      # Helm chart
│   │   ├── spec.md                     # contract
│   │   ├── clients/
│   │   │   ├── dotnet/                 # NuGet source
│   │   │   ├── node/                   # npm source
│   │   │   ├── python/
│   │   │   └── go/
│   │   └── images/                     # OCI image build context
│   ├── notifications/
│   ├── payments/
│   └── …
└── .github/workflows/
    ├── publish-cli.yml                 # publishes wave-platform CLI as npm package
    ├── publish-clients.yml             # publishes per-stack client SDKs
    └── release.yml                     # cuts a versioned blueprint release
```

The blueprint follows semver. Projects pin a major version; the blueprint promises additive-only changes within a major.

## 4. Per-stack folder shape — the universal contract

Every stack folder under `stacks/<name>/` follows the same structure so the protocol can plug into any stack:

```
stacks/<name>/
├── README.md                           # stack-specific quickstart
├── version.txt                         # semver, independently versioned
├── scaffold/
│   ├── _root/                          # files that go to the new project's repo root
│   │   ├── .gitignore
│   │   ├── README.md.tmpl
│   │   └── …
│   └── _service/                       # template for a single new service/feature/module
│       └── …                           # placeholder-substituted by 'wave run'
├── rules/                              # agent rules — fed into Claude Code via .claude/rules/
│   ├── 00-architecture.md
│   ├── 10-coupling.md
│   ├── 20-naming.md
│   ├── 30-error-handling.md
│   ├── 40-testing.md
│   ├── 50-coding-style.md
│   ├── 60-deploy.md
│   └── 99-anti-patterns.md
├── architecture/                       # rules consumed by the architecture-check tool
│   ├── forbidden-references.yaml       # "no cross-service imports" — stack-specific syntax
│   ├── coupling-thresholds.yaml        # max cyclomatic complexity, max file size, etc.
│   └── naming-rules.yaml
├── tests/
│   ├── e2e-harness.tmpl                # per-stack E2E framework adapter
│   ├── integration.tmpl
│   └── unit.tmpl
└── deploy/
    ├── Dockerfile.tmpl
    ├── helm-chart.tmpl/
    └── ci-workflow.yml.tmpl
```

**The contract — every stack folder MUST provide:**
- a `scaffold/` template tree (so `wave run` can produce service skeletons)
- a `rules/` directory (so agents have stack-specific guidance)
- an `architecture/` rules file (so the architecture check enforces correctness)
- a `tests/` adapter (so the test pyramid works on this stack)
- a `deploy/` template (so day-0 deploy works)

If a stack folder lacks one of these, `wave run --stack <stack>` errors with a clear message.

## 5. The bootstrap CLI: `wave-platform init`

```bash
npx wave-platform init my-new-project --stack dotnet --domain ecommerce
```

What this does:

1. Clone the blueprint at the latest stable release.
2. Copy `stacks/dotnet/scaffold/_root/` into the new project's git root.
3. Materialize `stacks/dotnet/rules/` into the new project's `.claude/rules/`.
4. Install the `wave` CLI as a project-local tool (`tools/wave`).
5. Write `wave-platform.yml` at the project root recording: blueprint version, active stack, active domain, list of cross-cutting services to consume.
6. Generate an initial `README.md` and Git-init the project.

After 30 seconds, the project has: agent rules, the wave CLI, the architecture check, the test-pyramid scaffold, and a `wave run` ready to drive the first feature.

```bash
cd my-new-project
git init && git commit -m "Initialise from wave-platform-blueprint vX.Y.Z"
wave run docs/agent-briefs/<spec>.md            # from second 1
```

## 6. Cross-cutting services as products

The cross-cutting services (Layer C) shipped with the blueprint are **products with versioned client SDKs**, not source code to copy.

For each service, the blueprint publishes:

| Artifact | What it does |
|---|---|
| **OCI image** at `ghcr.io/wave-platform/<svc>:<ver>` | the runnable service |
| **Helm chart** at `wave-platform/charts/<svc>` | deploys the service into a K8s cluster |
| **Client SDK per stack** | `Haworks.Audit.Client` NuGet, `@wave-platform/audit-client` npm, `wave_platform.audit` PyPI, etc. |
| **Spec doc** | the published API + event contract |

A new project consumes them by:
1. Adding the service via `wave-platform service add audit` — adds the Helm chart to the project's `infra/`, adds the relevant client SDK to its package manifest, and registers the service in the project's CDC consumer config (so audit starts capturing changes from day 1).
2. The project then calls `audit-svc` via the client SDK without ever vendoring the service code.

**Versioning discipline:**
- Each service is independently semver'd (`audit-svc v3.2.1`).
- The blueprint pins a tested set of service versions per blueprint release ("blueprint v5.4 ships with audit v3.2.1, notifications v2.8.0, …").
- Projects can override individual versions in their `wave-platform.yml` if they need.
- Breaking changes in any service require a new major; clients pin their compatible major.

This is the modularity discipline from the cross-cutting coupling audit, made operational.

## 7. Adapting the architecture check for any stack

`tools/architecture-check` reads `stacks/<active-stack>/architecture/forbidden-references.yaml`:

```yaml
# stacks/dotnet/architecture/forbidden-references.yaml
allowed_peers:
  - BuildingBlocks/Haworks.BuildingBlocks.csproj
  - Contracts/Haworks.Contracts.csproj
  - $self/*

# stacks/react/architecture/forbidden-references.yaml
allowed_peers:
  - "@wave-platform/*"
  - "$self/*"
forbidden_imports:
  - "from '../other-feature/*'"     # no cross-feature imports

# stacks/fastapi/architecture/forbidden-references.yaml
allowed_peers:
  - common/
  - contracts/
  - $self/*
```

Same script, different rules per stack. The check fails the build the same way regardless of stack — what changes is the import-graph definition.

## 8. The agent-rules system

Today: `.claude/rules/` per-project, hand-curated.

After the blueprint:

```
my-new-project/.claude/rules/
├── 00-architecture.md       # synced from blueprint stacks/<stack>/rules/
├── 10-coupling.md           # synced
├── …
└── local/
    ├── 00-this-project.md   # project-specific overrides
    └── 99-incidents.md      # project-specific lessons
```

The blueprint files are version-tracked; `wave-platform sync` updates them when the blueprint releases new versions. Project-local rules under `local/` always win conflicts.

**Feedback loop — how new learnings flow back to the blueprint:**

1. A session produces a learning ("BSD sed doesn't support `\U`; always use awk for case conversion in cross-platform scripts").
2. The session lands a small PR in the blueprint repo, adding the learning to the relevant `stacks/<stack>/rules/<file>.md`. CI of the blueprint validates the change (no contradictions with existing rules).
3. The blueprint releases a new patch (`5.4.1`). Notes call out the new rule.
4. Projects pick up the rule on next `wave-platform sync` (manual or automated via dependabot-style PR).

**Rules are versioned just like code.** The protocol is enforced; the rules are documented; the project gets both for free.

## 9. Translating the way-of-working to non-.NET stacks — concrete examples

### Example A: a React + TypeScript SPA project

```bash
npx wave-platform init dashboard-spa --stack react --domain b2b-saas
cd dashboard-spa
git init && git commit -m "init"

# Now drive features the same way as in this codebase
wave run docs/agent-briefs/dashboard-cart-spec.md     # spec describes a cart UI
```

What the wave does on a React project:
- L0 scaffold: a feature folder under `src/features/<feature>/` with components/hooks/api/types/styles
- N parallel tracks: each owns a sub-folder (e.g., `Components/`, `hooks/`, `api/`, `__tests__/`)
- Architecture check enforces no `import` from sibling features
- Test pyramid: Vitest unit + RTL integration + Playwright E2E (templates in `stacks/react/tests/`)
- Deploy: standalone container (Caddy + static), or Vercel/Netlify if the project chooses

The protocol is the same; the templates are React-shaped.

### Example B: a Python FastAPI service

```bash
npx wave-platform init analytics-api --stack fastapi --domain platform-internal
wave run docs/agent-briefs/analytics-ingest-spec.md
```

What changes:
- L0 scaffold: a Python package under `src/<feature>/` with `__init__.py`, `routers.py`, `models.py`, `service.py`, `tests/`
- DI: FastAPI's `Depends()` pattern; the orchestrator is the FastAPI app factory
- Architecture check: no cross-package imports beyond `common/` and `contracts/`
- Test pyramid: pytest unit + pytest-httpx integration + behave/E2E
- Deploy: same Helm + fly.toml templates, just pointing at a Python image

### Example C: a SwiftUI iOS app

```bash
npx wave-platform init mobile-companion --stack swift --domain consumer
wave run docs/agent-briefs/onboarding-flow-spec.md
```

What changes:
- L0 scaffold: a Swift package per feature under `Packages/<Feature>/`
- DI: protocol-based + a per-feature container
- Architecture check: no cross-package imports
- Test pyramid: XCTest unit + XCUITest E2E + snapshot tests
- Deploy: TestFlight/App Store via fastlane

The protocol's parallel-track contract still applies — agents work on disjoint Swift packages, claim races are race-safe, sentinel commits work the same way (Git is Git).

## 10. Implementation phases

This is multi-month work, but the value is unlocked at each phase.

| Phase | Scope | Effort | Spec |
|---|---|---|---|
| **P0** | Extract `wave` + `SUPERPROMPT` + agent rules into a `wave-platform-blueprint` GitHub repo (no stacks yet — just Layer A). Validate on this very project (consume from blueprint instead of bundled). | 3-5 days | `blueprint-p0-extract-spec.md` |
| **P1** | Add `dotnet` stack folder. This codebase becomes the canonical reference. The architecture check, scaffold templates, agent rules all migrate from `docs/agent-briefs/` to `stacks/dotnet/`. | 2-3 days | `blueprint-p1-dotnet-stack-spec.md` |
| **P2** | Publish cross-cutting services as containers + clients. `audit-svc`, `notifications-svc`, `cdc-svc` etc. each get a Helm chart + client SDK + spec. | 2 weeks | `blueprint-p2-services-publish-spec.md` |
| **P3** | Add `react` stack folder. Validate by `wave-platform init`-ing a small React project and shipping one feature via `wave run`. | 1 week | `blueprint-p3-react-stack-spec.md` |
| **P4** | Add `fastapi` (Python) + `go` stacks. | 1.5 weeks | `blueprint-p4-other-backends-spec.md` |
| **P5** | Add `swift` + `kotlin` mobile stacks. | 2 weeks | `blueprint-p5-mobile-stacks-spec.md` |
| **P6** | Establish the rules-feedback flow: blueprint CI auto-prompts after every learning-bearing PR; project sync mechanism via dependabot-style updates. | 1 week | `blueprint-p6-rules-flow-spec.md` |
| **P7** | Documentation site (docs.wave-platform.dev or similar). Tutorials per stack. Reference architectures. | 2 weeks | `blueprint-p7-docs-spec.md` |

Total: ~3 months for a complete blueprint. P0 + P1 alone (~1 week) gives you a usable base. P2 (services published) takes you to "any new .NET project starts with the cross-cutting stack solved" — the biggest single value step.

## 11. Test plan

The blueprint itself has rigorous tests, scaled per layer:

- **Layer A**: the existing wave stress-test (`/tmp/stress-claim/stress.sh`) runs against any project that consumes the blueprint. 10/10 invariants must hold.
- **Per-stack folder**: a `stacks/<name>/test.sh` that does `wave-platform init` of a temp project, then runs `wave run` with a sample spec, then asserts the project compiles + tests + deploys (against a kind/k3d cluster).
- **Cross-cutting services**: each service has its existing test suite (per `cdc-service-spec.md` § 10 etc.), plus a "consumed by a fresh project" E2E.
- **Blueprint integration**: a nightly job that does `wave-platform init` per stack, generates a small project, runs all checks. Catches regressions in either the blueprint or stack folders.

## 12. Operating model — how this stays maintained

Maintenance discipline is everything. The blueprint dies the day learnings stop flowing back.

| Concern | Mechanism |
|---|---|
| **New learnings flow back** | Every project's `.claude/rules/local/99-incidents.md` is reviewed monthly. Generalisable lessons go into a blueprint PR. |
| **Stack folders stay current** | Each stack has a designated maintainer + a "reference project" that runs the stack's CI nightly. Drift between the reference project and the stack folder is caught daily. |
| **Cross-cutting services don't fork** | Each service has ONE source of truth (the blueprint repo). Projects consume containers + SDKs, never source. The architecture check forbids vendoring. |
| **Versioning stays clean** | Semver enforced by CI. Breaking changes require RFC + 30-day notice. Patch bumps are auto-released. |
| **Documentation stays current** | `docs/` lives in the blueprint repo. Every feature change requires a docs change in the same PR. |

## 13. Reference projects to mirror

- `create-react-app` / `vite` for the bootstrap CLI shape (`npx wave-platform init`)
- `kubernetes-the-hard-way` for layered architecture documentation
- `helm/charts` for the chart-publishing flow
- `bazel` for cross-stack build conventions (we don't use Bazel, but the philosophy of "one rule set across stacks" is right)
- `clean-architecture-template` GitHub repos in various languages — for stack-folder templates
- the existing `wave` tool in this codebase (the foundation)
