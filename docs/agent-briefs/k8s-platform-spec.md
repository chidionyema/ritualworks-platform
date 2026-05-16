# K8s Platform — End-to-End Spec

**Status:** Vision spec (not implemented). Current production deployment uses Fly.io. See [GETTING-STARTED.md](../GETTING-STARTED.md) for local dev (Aspire) and [BACKLOG.md](../BACKLOG.md) for K8s migration priority.

## 1. Goal & non-goals

### Goal

A production-grade Kubernetes cluster that runs the entire haworks platform (and any future platform built on the same primitives) with a single operating model — same manifests, same observability, same secrets flow, same deploy pipeline — across:

- **Major clouds:** AWS (EKS), GCP (GKE), Azure (AKS), DigitalOcean (DOKS), Linode (LKE)
- **On-prem / bare metal:** k3s or RKE2 on Linux hosts (single-server demo to multi-node HA cluster)
- **Local dev:** kind / k3d on a developer's laptop

The portability constraint is the design driver. Anything that ties the platform to one cloud is a defect.

### Non-goals

- Multi-cloud active-active (one cluster spans clouds). Not needed; cluster-per-region is fine.
- Cluster federation (KubeFed). Operationally heavy; revisit only if a real need emerges.
- Service mesh (Istio / Linkerd). Adds a layer of failure modes; the platform's observability + auth needs are met by less invasive means today.
- Custom Kubernetes distribution. Use upstream + upstream-compatible (k3s).

## 2. Architecture at a glance

Three layers, each cloud-agnostic by design:

```
┌─────────────────────────────────────────────────────────────────┐
│  Layer 3 — Application (cloud-agnostic by construction)        │
│  Helm charts per microservice • ArgoCD-managed • cert-mgr TLS  │
│  Postgres via CloudNativePG • RabbitMQ Cluster Operator        │
│  Redis via Bitnami chart • Vault via vault-operator            │
│  External Secrets Operator → Vault → K8s Secrets               │
├─────────────────────────────────────────────────────────────────┤
│  Layer 2 — Cluster Addons (same across all targets)            │
│  Cilium CNI • Ingress-NGINX • cert-manager • External-DNS      │
│  Prometheus/Grafana/Loki/Tempo (matches existing OTLP)         │
│  ArgoCD • Velero (backups) • OpenTelemetry Collector           │
├─────────────────────────────────────────────────────────────────┤
│  Layer 1 — Cluster Provisioning (cloud/on-prem dependent)      │
│  Cloud:  Cluster API (CAPI) with provider plugins              │
│           AWS: EKS via capa  Azure: AKS via capz                │
│           GCP: GKE via capg  DO: DOKS via capdo                 │
│  On-prem: k3s installer (single command per node, or Ansible)  │
│  Local:   kind / k3d  (dev only)                               │
└─────────────────────────────────────────────────────────────────┘
```

**The crucial property:** Layers 2 and 3 are *identical* across all Layer 1 implementations. A pod running on EKS and a pod running on a Raspberry Pi k3s cluster see the same secrets flow, the same DNS, the same ingress, the same metrics pipeline. Operators learn one stack.

## 3. Portability strategy — concrete rules

| Concern | Rule | What it forbids |
|---|---|---|
| **Networking** | Use Kubernetes Services + Ingress only. CNI = Cilium. | No `LoadBalancer` Service type tied to a specific cloud's annotations; use ingress-controller with a single LoadBalancer (cloud) or MetalLB (on-prem). |
| **Storage** | Use PersistentVolumeClaims with the cluster's default StorageClass. Backup via Velero. | No `aws-ebs` / `gce-pd` / `azure-disk` volumes referenced directly. CSI drivers handle the rest. |
| **DNS** | External-DNS reads Service/Ingress annotations. Backend = Cloudflare (universal) or per-cloud DNS. | No `aws-route53` references in app manifests. |
| **TLS** | cert-manager + Let's Encrypt (HTTP-01 or DNS-01 via Cloudflare). | No cloud-managed cert services. |
| **Secrets** | Vault (already deployed) + External Secrets Operator. | No K8s Secret YAMLs committed to git. No cloud secret-manager (AWS SSM, GCP Secret Manager). |
| **Postgres** | CloudNativePG operator. WAL backup to S3-compatible storage (works on any cloud + MinIO on-prem). | No RDS / Cloud SQL / Azure DB. |
| **RabbitMQ** | RabbitMQ Cluster Operator. | No Amazon MQ / GCP Pub-Sub-as-replacement. |
| **Redis** | Bitnami Redis Helm chart (works everywhere) OR KubeBlocks Redis. | No ElastiCache / Memorystore. |
| **Object storage** | S3-compatible API only. MinIO on-prem; AWS S3 / GCS-S3-bridge / Azure Blob via S3 gateway elsewhere. | No native cloud SDK calls (use AWS SDK with custom endpoint). |
| **Logs / metrics / traces** | OpenTelemetry → Tempo + Loki + Prometheus, all in-cluster. | No CloudWatch / Stackdriver / App Insights as primary. |
| **CI/CD** | GitHub Actions builds images; ArgoCD pulls + applies (GitOps). | No cloud-specific deploy systems (CodeDeploy, Cloud Build) as primary. |
| **Cluster provisioning** | Cluster API (cloud) or k3s (on-prem). | No Terraform-per-cloud-with-different-modules. |

**The acid test:** can you `terraform destroy` a whole cloud cluster, spin up a k3s on a single VPS, and have the platform running again in < 1 hour using only `argocd app sync`? If yes — portable. If no — find the lock-in and fix it.

## 4. The single repo layout

A new top-level `infra/` directory in the platform repo:

```
infra/
├── clusters/                          # one dir per environment
│   ├── dev-local/                     # k3d, single-node, dev
│   │   ├── cluster.yaml               # k3d config
│   │   └── argocd-bootstrap.yaml      # what argocd starts managing
│   ├── prod-aws/                      # CAPI manifest for EKS
│   ├── prod-on-prem/                  # k3s nodes + ansible
│   └── prod-gcp/                      # CAPI manifest for GKE
├── addons/                            # Helm releases applied to every cluster
│   ├── cilium/
│   ├── ingress-nginx/
│   ├── cert-manager/
│   ├── external-dns/
│   ├── external-secrets/
│   ├── argocd/                        # bootstrap-only; argocd manages itself afterwards
│   ├── prometheus-stack/              # kube-prometheus-stack
│   ├── loki/
│   ├── tempo/
│   ├── otel-collector/
│   └── velero/
├── stateful/                          # operator + cluster CRs for shared infra
│   ├── cnpg/                          # CloudNativePG operator
│   ├── postgres-clusters/             # one Cluster CR per service DB
│   │   ├── audit.yaml                 # mirrors current audit DB
│   │   ├── catalog.yaml
│   │   └── …
│   ├── rabbitmq-cluster/
│   ├── redis-master/
│   └── vault/                         # vault-operator + Vault HA
└── apps/                              # one Helm chart per microservice
    ├── audit/
    │   ├── Chart.yaml
    │   ├── values.yaml                # defaults
    │   ├── values-prod.yaml           # prod overrides (resource limits, replicas)
    │   ├── values-dev.yaml            # dev overrides (small replicas)
    │   └── templates/
    │       ├── deployment.yaml
    │       ├── service.yaml
    │       ├── ingress.yaml
    │       ├── externalsecret.yaml    # pulls Vault paths into K8s Secrets
    │       └── servicemonitor.yaml    # Prometheus scrape config
    ├── catalog/
    └── …
```

**The discipline:** every service's Helm chart is a structurally identical template (deployment + service + ingress + externalsecret + servicemonitor). Adding a new service = `wave run` generates the chart from the scaffold, drops it into `infra/apps/<svc>/`, ArgoCD picks it up automatically.

## 5. Migration from Fly

Fly stays as the production deploy target until the K8s platform is proven. Migration is incremental:

1. Stand up dev-local k3d cluster with all addons + stateful services. Smoke-tested with one or two non-critical services (e.g., search).
2. Stand up the first cloud cluster (probably AWS) — same addons, same stateful CRDs. Run a single service against it in shadow mode (parallel deploy, no production traffic).
3. Move ONE service at a time from Fly to K8s. Cut traffic via DNS once stable. Fly app remains as fallback for one release cycle.
4. Once all services migrated: stand up the on-prem k3s cluster as a third deploy target. Validate the same set of services against it.
5. Decommission Fly per service as confidence builds.

## 6. Implementation phases

Each becomes its own spec — too much for one wave. This document is the index.

| Phase | Scope | Key spec | Effort |
|---|---|---|---|
| **P0** | Local-only baseline | `k8s-p0-local-baseline-spec.md` | 1 week |
| **P1** | Helm chart per service | `k8s-p1-app-charts-spec.md` | 1 week |
| **P2** | Stateful services on K8s | `k8s-p2-stateful-spec.md` | 2 weeks |
| **P3** | First cloud cluster (AWS) | `k8s-p3-cloud-aws-spec.md` | 1 week |
| **P4** | GitOps + observability | `k8s-p4-gitops-obs-spec.md` | 1 week |
| **P5** | On-prem k3s cluster | `k8s-p5-on-prem-spec.md` | 1 week |
| **P6** | Production hardening | `k8s-p6-hardening-spec.md` | 2 weeks |
| **P7** | Cut traffic over | `k8s-p7-migration-spec.md` | per-service |

### Phase 0 — Local baseline (the unblocker)

**Deliverable:** a single command that brings up a k3d cluster, installs all Layer 2 addons, runs a Postgres cluster, Vault, and one of the existing services (audit-svc) end-to-end. Nothing cloud-specific.

```bash
make -C infra/clusters/dev-local up    # 5 min, all-in-one
kubectl port-forward svc/audit-svc 8080:80
curl http://localhost:8080/health      # 200 OK
```

This proves the architecture before anything goes to a cloud. From here, every other phase is "swap Layer 1, keep Layers 2-3."

### Phase 1 — Helm chart per service

`wave run docs/agent-briefs/k8s-p1-app-charts-spec.md` — one parallel track per service, each producing a Helm chart from the canonical template. The wave-tool's scaffold templates extend to include a `chart-template/` mirror of `infra/apps/<feature>/`.

After P1: every service has a chart, the chart deploys to k3d, the k3d cluster mirrors the production app topology.

### Phase 2 — Stateful services

CloudNativePG cluster CRs per service DB. Migration: dump from Fly's Postgres, restore into CNPG cluster. Validate.

RabbitMQ Cluster Operator. Redis Bitnami chart. Vault is already cloud-agnostic — port the existing config.

### Phase 3 — First cloud cluster

CAPI + capa (Cluster API for AWS) provisions an EKS cluster. Apply the same addons via ArgoCD bootstrap. Same Helm charts. The cluster is identical to dev-local except scale.

### Phase 4 — GitOps + observability

ArgoCD managing everything from `infra/`. Prometheus + Grafana + Loki + Tempo, OTLP → these via OpenTelemetry Collector (the OTLP endpoint env var the existing services already emit to).

### Phase 5 — On-prem cluster

k3s install on Linux nodes (single-server quickstart, then multi-node HA). Same addon set + same Helm charts. Storage: longhorn or MinIO + cnpg backups.

### Phase 6 — Hardening

NetworkPolicies, PodSecurityStandards, autoscaling (HPA/VPA), resource quotas, PDBs, multi-zone in cloud, MetalLB on-prem.

### Phase 7 — Migration

Per-service: deploy to K8s, shadow traffic, validate, flip DNS, decommission Fly app. One at a time, in dependency order (audit → notifications → search → content → others → orders/payments/checkout last).

## 7. Operator UX — seamless across all targets

The "any cloud + on-prem" promise breaks the moment operators have to remember target-specific commands. The surface must be uniform: same verbs, same output, same troubleshooting. Implemented as a single CLI (`platform`, mirroring how `wave` already abstracts parallel-agent waves).

### The CLI contract

```bash
# Provisioning — the only target-specific concept; the CLI dispatches internally
platform up <cluster-name>            # spins up cluster per infra/clusters/<cluster-name>/
platform down <cluster-name>          # tears it down
platform list                         # all known clusters

# Application lifecycle — identical across all targets
platform deploy <feature>             # deploy one service to the active cluster
platform deploy --all                 # deploy everything (ArgoCD bootstrap)
platform status                       # one-line-per-service health dashboard
platform status <feature>             # detailed status for one service

# Day-2 operations — identical across all targets
platform logs <feature>               # follow logs (kubectl logs -f wrapper, but feature-aware)
platform logs <feature> --since 1h    # historical
platform shell <feature>              # exec into a running pod
platform port-forward <feature>       # local port-forward to the service
platform restart <feature>            # rolling restart
platform scale <feature> --replicas N # adjust replica count

# Cluster-level
platform context <cluster-name>       # switch active kubectl context
platform observability                # opens Grafana in browser
platform secrets <feature>            # show what Vault paths are wired (no values)
platform doctor                       # runs all readiness checks; prints what's broken

# Disaster recovery
platform backup <cluster-name>        # trigger Velero backup
platform restore <cluster-name> <backup-id>
```

Same commands work whether the active cluster is `dev-local`, `prod-aws`, `prod-on-prem`. Every command exits non-zero on failure with a one-line cause, so it composes in shell and CI.

### The `platform doctor` check (the single most important command)

Runs through every layer and reports what's broken:

```
$ platform doctor
[cluster]    ✓ k3d cluster 'dev-local' reachable
[cilium]     ✓ CNI healthy on all 1 nodes
[ingress]    ✓ ingress-nginx controller running, LoadBalancer at 127.0.0.1:80
[cert-mgr]   ✓ cert-manager running, 0 Certificate resources in error state
[vault]      ✓ Vault unsealed, 1/1 active replicas
[argocd]     ✓ ArgoCD up; 12 Applications synced, 0 OutOfSync
[postgres]   ✓ 9 CloudNativePG clusters healthy
[rabbitmq]   ✓ RabbitMQ cluster healthy, 1 partition
[apps]       ✓ 11/11 services Ready (Pod count matches Deployment spec)
[backups]    ⚠ Last Velero backup 26h ago (target: < 24h) — check schedule

summary: 1 warning, 0 errors. Cluster operationally healthy.
```

This is what an operator runs first when something feels off. The output is uniform across cloud + on-prem.

## 8. Test plan — fully testable platform

Tests are structured as four layers, each running in CI:

### 8.1 Layer 1 — cluster bringup (per-target)

| Test | Target | What it proves |
|---|---|---|
| `make -C infra/test/bringup-local` | k3d (CI runner) | A laptop / CI runner can stand up the platform from zero in < 10 min. Runs every PR. |
| `make -C infra/test/bringup-aws` | EKS via CAPI in a sandbox AWS account | Cluster API + capa flow works end-to-end. Runs nightly (cost-bound). |
| `make -C infra/test/bringup-on-prem` | k3s on a fixed-IP test VM | k3s flow works. Runs weekly. |

Each test is the same `platform up <ctx> && platform deploy --all && platform doctor` sequence. If `doctor` passes, the test passes.

### 8.2 Layer 2 — addon-level tests

For each addon (Cilium, ingress, cert-manager, etc.), a smoke test asserts its specific contract:
- `cilium`: pod-to-pod across nodes, network-policy enforcement
- `cert-manager`: Certificate resource resolves to a real issued cert
- `external-secrets`: ExternalSecret pulls from Vault → K8s Secret materializes
- `cloudnative-pg`: streaming replication, failover, point-in-time restore
- `velero`: backup → delete namespace → restore → data intact

These run on `dev-local` in CI on every PR that touches addon manifests.

### 8.3 Layer 3 — application E2E (the existing E2E framework, ported)

The E2E framework spec'd in `e2e-framework-spec.md` runs against the K8s cluster, not just Aspire-in-process. Same golden journeys (CustomerCheckout, RefundJourney, etc.), same assertions — but the AppHostFixture's `DistributedApplicationTestingBuilder` is swapped for a K8s-backed harness that talks to in-cluster services via port-forward.

This is the proof that "applications run on the platform" — same suite on Aspire, dev-local, EKS, and on-prem k3s. If they all pass, the platform is doing its job.

### 8.4 Layer 4 — chaos + portability tests (the hard ones)

Run on a sacrificial cluster, weekly:

| Test | What it injects | Pass criterion |
|---|---|---|
| **Pod chaos** | Kill 1 pod per Deployment in random order, 1/min for 10 min | Service health stays green; no event lost; tests still pass |
| **Node chaos** | Cordon + drain a worker node | Pods reschedule within 2 min; service recovers; data intact |
| **Network partition** | Block traffic between two nodes for 5 min | Cluster recovers; CNPG primary re-elected if needed; no data loss |
| **Cluster destroy + restore** | `platform down prod-aws-staging; platform up prod-aws-staging; platform restore --latest` | All services running with restored data within 30 min |
| **Cross-target portability** | Backup on `dev-local`; restore on `prod-aws-staging` | Restored cluster passes E2E suite within 1 hour of bringup |

The cross-target portability test is THE acid test — if a backup taken from a k3d cluster restores cleanly to EKS, the platform really is portable.

## 9. Continuous validation

A `.github/workflows/k8s-platform.yml` runs:

| Trigger | What runs |
|---|---|
| PR touching `infra/` | Bringup-local + addon smoke + app E2E |
| Merge to main | Bringup-local + bringup-aws + full test suite |
| Nightly cron | All cluster targets + chaos + portability |
| Release tag | Backup + restore on each target as final gate |

Failure on any of these fails the merge / release. The platform is not "ready to ship" until each test target is green.

## 10. Observability

OpenTelemetry remains the standard. The existing `OTEL_EXPORTER_OTLP_ENDPOINT` env var continues to work — Helm chart sets it to `http://otel-collector.observability.svc.cluster.local:4317`. No service code changes.

Prometheus scrapes via `ServiceMonitor` CRDs (one per Helm chart). Grafana dashboards live in `infra/addons/prometheus-stack/dashboards/`.

## 11. Failure modes & recovery

| Failure | Recovery |
|---|---|
| Cloud control plane lost | CAPI re-provisions. ArgoCD re-applies. Stateful data restored from Velero backup. < 30 min for a non-trivial cluster. |
| On-prem node hardware failure | Drain node, replace, k3s rejoins. CNPG re-elects primary. |
| Bad release | ArgoCD rolls back to previous git ref. |
| Vault loss | DR replica + recovery keys (already designed in vault-101 worktree). |
| Region outage (cloud) | Cluster-per-region; DNS shifts traffic. (Multi-region active-active is a non-goal.) |

## 12. Parallel decomposition — one-day delivery via `wave run`

The phase ladder (P0 → P7) is the conceptual story. The **execution plan** is parallel-first: 8 disjoint-file-scope tracks running simultaneously after a 30-minute L0 setup, integration at the end. With 8 gemini agents in parallel and 3-4 hour tracks, total wall-clock ≈ 4-6 hours.

This is what `wave run docs/agent-briefs/k8s-platform-spec.md` produces. `WAVE_MODE=modify`. The brief should set `BASE_BRANCH=feat/k8s-platform` and `TRACKS=(T1 T2 T3 T4 T5 T6 T7 T8)`.

### Day 1 — what fits in a working day (8 parallel tracks)

| Track | Owns (disjoint) | Hours | Done check |
|---|---|---|---|
| **T1** Cluster bringup local + base CNI/ingress | `infra/clusters/dev-local/**`, `infra/addons/cilium/**`, `infra/addons/ingress-nginx/**`, `infra/addons/cert-manager/**`, `infra/addons/external-dns/**` | 4 | `make -C infra/clusters/dev-local up && kubectl get nodes` returns Ready; `kubectl get pods -A` all Running |
| **T2** Stateful operators + Cluster CRs | `infra/stateful/cnpg/**`, `infra/stateful/rabbitmq/**`, `infra/stateful/redis/**`, `infra/stateful/vault/**`, `infra/stateful/postgres-clusters/*.yaml` | 4 | `kubectl get cluster.postgresql.cnpg.io -A` all healthy; `kubectl get rabbitmqcluster -A` healthy; vault unsealed |
| **T3** Observability stack | `infra/addons/prometheus-stack/**`, `infra/addons/loki/**`, `infra/addons/tempo/**`, `infra/addons/otel-collector/**`, `infra/addons/grafana-dashboards/**` | 3 | Grafana datasources green; one ServiceMonitor scraping; OTLP endpoint accepts traces |
| **T4** App-chart template + scaffold extension | `docs/agent-briefs/_TEMPLATE/scaffold/infra/**` (new template tree), `docs/agent-briefs/wave` (extend `apply_scaffold` to also write Helm chart per service) | 3 | a fresh `wave run` for any new feature now also produces `infra/apps/<feature>/Chart.yaml` + values + templates that pass `helm lint` |
| **T5** Per-service Helm charts (existing 9 services) | `infra/apps/audit/**`, `infra/apps/notifications/**`, `infra/apps/payments/**`, `infra/apps/search/**`, `infra/apps/content/**`, `infra/apps/identity/**`, `infra/apps/catalog/**`, `infra/apps/orders/**`, `infra/apps/bff/**`, `infra/apps/checkout/**` | 4 | each chart `helm install`s into dev-local; `kubectl get pod -l app=<svc>` Ready; service responds at its in-cluster DNS |
| **T6** GitOps (ArgoCD) | `infra/addons/argocd/**`, `infra/clusters/dev-local/argocd-bootstrap.yaml`, `infra/argocd-applications/*.yaml` | 3 | `argocd app list` shows all 9 apps Synced + Healthy with no manual `helm install` invocations |
| **T7** `platform` CLI | `tools/platform` (bash CLI), `tools/platform-completions/`, `docs/runbooks/platform-cli.md` | 4 | every verb listed in § 7 works against dev-local: `platform up dev-local`, `platform deploy --all`, `platform doctor`, `platform logs <svc>`, `platform shell <svc>`, etc. |
| **T8** Test framework + CI workflow | `infra/test/bringup-local.sh`, `infra/test/addon-smokes/**`, `infra/test/portability-acid-test.sh`, `.github/workflows/k8s-platform.yml` | 3 | bringup-local test passes from a clean machine in CI; addon smokes (cilium, cert-mgr, external-secrets, cnpg, velero) all green |

**End of day 1 deliverable:** dev-local k3d cluster runs the full haworks platform via ArgoCD, with `platform doctor` reporting all subsystems green and the E2E suite (Phase 3c) passing against the in-cluster deploy.

### Day 2+ — deferred to follow-up waves (not day-1 achievable)

| Track | Why deferred | Estimated effort |
|---|---|---|
| **T9** Cloud cluster bringup (AWS via CAPI) | Cloud provisioning has 20-30 min iteration cycles + needs sandbox AWS account + CAPI is its own learning curve. Cannot fit day-1 alongside the other 8 tracks. | 1 day, separate wave |
| **T10** On-prem k3s + MetalLB | Needs a real Linux host (or VM cluster) to test against. | 0.5 day, separate wave |
| **T11** Chaos + portability acid tests (L4) | Needs a working cloud cluster to inject chaos against. Built once T9 is done. | 0.5 day |
| **T12** Cut traffic from Fly | Per-service work; sequenced behind T9 (cloud cluster proven). | per-service, ~1 day total |

**The architectural property that makes day-2 feasible:** every artifact day-1 produces (charts, manifests, addons, tests) is target-agnostic. T9 / T10 / T11 just swap Layer 1; Layers 2-3 carry over verbatim.

### Disjoint-scope rules (the parallel-execution contract)

- **Each track owns its file paths exclusively.** A T2 agent writing `infra/stateful/cnpg/Chart.yaml` will never collide with a T3 agent writing `infra/addons/loki/values.yaml`.
- **The shared file is `infra/clusters/dev-local/argocd-bootstrap.yaml`** — owned by T6 (ArgoCD). All other tracks list their resources but don't touch the bootstrap manifest. T6 references them by file path.
- **The shared bash CLI is `tools/platform`** — owned by T7. T8's tests invoke it but don't modify it.
- **Cross-track integration is via convention (file layout) not via shared types.** No track imports another track's code.

### Integration step (after all 8 tracks merge to `feat/k8s-platform`)

Single integration commit on the merge branch:
```bash
make -C infra/clusters/dev-local up
platform deploy --all
platform doctor
infra/test/bringup-local.sh
```

If all four pass, the day-1 wave is done. PR `feat/k8s-platform → main` is the rollup.

### Reference projects to mirror

- `kubernetes-the-hard-way` for understanding what each addon does
- `cluster-api` book for cloud provisioning (T9 reference)
- `argo-cd` for GitOps patterns (T6)
- `kube-prometheus-stack` Helm chart for observability (T3)
- `cloudnative-pg` for Postgres (T2)
- `vault-101` (already in this repo's worktree) for Vault setup (T2)
- existing `tools/wave` for the platform CLI shape (T7 — same bash-with-subcommands pattern)
