# ADR-0002: Aspire for Local Dev, kind+ArgoCD for Production-Shape Demo

**Status:** Partially superseded — local dev uses Aspire (unchanged); production deployment uses **Fly.io** (not Kind/K8s). K8s remains a future option; see `docs/agent-briefs/k8s-platform-spec.md`.
**Date:** 2026-05-02
**Deciders:** chidionyema

## Context

Two competing requirements:

1. **Daily dev iteration must be fast.** A new service shouldn't add 30 s to startup. Live reload is non-negotiable. The current `dotnet run --project src/haworks.AppHost` (Aspire) hits this bar — full stack up in <30 s with the dashboard, traces, and logs all integrated.

2. **The production deployment story must be credible.** Hiring managers evaluating "can this person actually run microservices in production" need to see real Helm charts, real ArgoCD GitOps, real Vault Kubernetes auth — not just a `dotnet run` and a hand-wave.

These pull in different directions. Aspire is brilliant for dev ergonomics but is not a production deployment tool. Pure Kubernetes (with Tilt or Skaffold) gives production parity but at high cognitive cost for daily iteration.

## Decision

**Run two parallel paths from the same repository.** Both stay green at all times. Neither replaces the other.

- **`dotnet run --project deploy/aspire`** — daily dev. Aspire AppHost composes 7 services + infra. <30 s startup, live reload via `dotnet watch`, Aspire dashboard for observability.
- **`make k8s-up`** — production-shape demo. kind cluster + ArgoCD App-of-Apps + per-service Helm charts + Vault K8s auth + observability stack. ~3 min startup. Run on demand or in CI.

Same image artifacts, same Helm `values.yaml` shape, same Vault policies, same OTel pipeline. Only the runtime substrate differs.

The Helm charts and ArgoCD `Application` resources are written **as if targeting AWS EKS** — same image refs, same `ServiceAccount`-based Vault auth, same `HorizontalPodAutoscaler`, same `NetworkPolicy`, same `PodDisruptionBudget`. Only `values.prod.yaml` differs (kind uses `localhost:5000` registry; prod uses ECR/GHCR). The README explicitly says: *"To deploy to EKS, point ArgoCD at this repo and update `values.prod.yaml`. No code changes."*

## Options Considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Aspire dev + kind/Helm/ArgoCD prod-shape (chosen)** | Best-of-both-worlds: fast dev + credible prod demo; Helm/ArgoCD deployable to EKS unchanged. | Two paths to maintain. **Mitigated** by sharing image artifacts and validating both in CI. | **Chosen.** |
| Aspire only | Simplest dev story. | No production credibility — hiring managers see "demo project that won't scale." | Rejected. Defeats the portfolio purpose. |
| kind/Tilt/Skaffold only | True production parity for dev. | Cognitive tax (kubectl, manifests, ingress, image-pull policies) per dev is high. Aspire's dashboard + service-discovery wiring lost. | Rejected. Bad ROI for a solo project. |
| Aspire dev + EKS prod ($70/mo control plane + compute) | Full production deployment. | Pays AWS to host an idle portfolio piece. Recruiter sees "hosted on EKS" but never inspects the deployment. | Rejected on cost. The Helm/ArgoCD work is what they evaluate, not whether it's running on AWS. |
| Docker Compose for both | Simplest. | Loses dashboard, OTel auto-wiring, service-discovery env injection, WaitForCompletion graph. Loses production credibility entirely. | Rejected. |
| Pure Aspire-Azure deploy (`azd up`) | Azure-native, integrated. | Locks to Azure; doesn't co-exist with ArgoCD GitOps; preview-tier maturity. | Rejected. |

## Consequences

### Positive
- Devs run one command (`dotnet run --project deploy/aspire`) and have everything.
- Recruiters can see real Kubernetes manifests and a real GitOps deploy flow without paying cloud costs.
- Same images, same charts → "deploy to EKS" is one config change away. Demonstrably so.
- ArgoCD UI screenshot showing 7 services synced from Git is a portfolio artifact.
- Vault K8s auth in the prod path proves we understand cloud-native secrets management.

### Negative
- Two paths must be kept in sync. **Mitigation:** CI runs both `dotnet run --project deploy/aspire` smoke and `make k8s-up` smoke on every PR to `deploy/`.
- Aspire's `AddProject<T>` doesn't work cleanly across `.sln` boundaries in the monorepo — see [ADR-0001](./0001-strict-monorepo.md) Risk #5. **Mitigation:** `--override <svc>=local` pattern; default is `AddContainer(image-tag)` with pinned digests.
- Helm chart authoring takes time vs `kubectl apply`. **Mitigation:** one cookie-cutter chart in `deploy/helm/<service>/`; per-service customization is `values.yaml` only.

### Neutral
- The presence of two deployment paths might confuse new contributors. **Mitigation:** `docs/runbooks/which-runtime-when.md` explains the decision.

## Notes

Optional public demo: ~$12/mo DigitalOcean managed Kubernetes (1 node, 2 vCPU, 4 GB RAM) hosts the kind manifests with minimal value-file changes. Provides a live URL for portfolio viewers without paying EKS prices.

Reference: [02-platform.md § Two Parallel Deployment Paths](../02-platform.md#two-parallel-deployment-paths)
