# Vault Credential Delivery — Local Dev → Production

How services in this platform get their Vault AppRole credentials, why the
local dev path is intentionally different from production, and the migration
path between them.

> **Status:** Layer 1 is live (the slow-but-correct gate). Layer 2 was
> attempted and reverted — it's documented below as the cautionary "don't
> try the obvious thing" entry. Layer 3 is the documented prod target —
> implemented in the Helm charts at
> [`deploy/helm/identity-svc/`](../../deploy/helm/identity-svc/) but not yet
> exercised because the platform isn't deployed to a real cluster.

---

## 1. The problem

Every service that uses Vault needs two pieces of information at boot:
- A **`role_id`** — long-lived, identifies which AppRole this service is
- A **`secret_id`** — short-lived, proves this service is allowed to use that AppRole

Services exchange these for a Vault token, then use the token to read
KV-stored secrets (JWT signing key, OAuth client secrets, Stripe API key, etc.)
into their `IConfiguration`.

The question is: **how do `role_id` and `secret_id` get onto the service's
filesystem?** Three answers, ordered by how production-shaped they are.

---

## 2. The three layers

| Layer | Where it lives | What writes the creds | Pros | Cons |
|---|---|---|---|---|
| **1** | dev only (current) | `WaitForCompletion(vaultSeed)` Aspire gate blocks service launch until vault-init AND vault-seed have both finished | "Just works" — files exist, AppRole is configured, KV secrets seeded | Aspire 9.x reconciler bug on macOS makes the gate hold for 25-30s even though the seed finishes in 3s |
| **2** | dev only **(reverted — see §4)** | `vault-init` writes files; service uses `WaitFor(vault)` and bootstrap polls for files | *Sounded right* — start in parallel, ~10s — but identity loses the AppRole-login race because file-existence ≠ Vault-server-ready | crash-loops on AppRole 400; identity never comes online |
| **3** | prod (planned) | Vault Agent Injector sidecar writes credentials into a projected volume on pod start; service reads from `/vault/secrets/role_id` etc. | Industry standard for k8s + Vault; secrets never touch the host fs; rotation is push-based (sidecar restarts the pod); also fixes the dev-side gate problem because the sidecar handles all timing | Requires k8s + the Vault Agent Injector helm chart deployed to the cluster |

---

## 3. Layer 1 — `WaitForCompletion(vaultSeed)` (deprecated)

**What it was:** the AppHost had:
```csharp
var identity = builder.AddProject<Projects.Identity_Api>("identity-svc")
    .WaitForCompletion(vaultSeed)        // <- blocks identity's process spawn
    .WithReference(identityDb)
    .WithEnvironment("Vault__Enabled", "true");
```

**The bug:** Aspire 9.x's container reconciler emits this confusing error on
macOS for one-shot containers that exit cleanly:
```
failed to start Container ... error: status of container ... is 'exited' (was expecting 'exited')
```
The reconciler eventually figures out that "exited" *is* the success state for
a one-shot, but it takes 20-25s to propagate. During that window, every
service gated on `WaitForCompletion(vaultSeed)` sits idle.

**Symptom seen by users:** `dotnet run --project deploy/aspire` brings up
infra in ~3s, says "Distributed application started", then identity-svc
takes another 25-30s before it shows `Running` in the dashboard.

**Why we used it anyway in early phases:** simplest mental model — "creds
must exist before service starts" — and we didn't yet know
`VaultConfigBootstrap.LoadAsync` already polled for files.

---

## 4. Layer 2 — Reverted (`WaitFor(vault)` + bootstrap file-poll)

> **Outcome:** tried, broke identity-svc completely, reverted within a few
> minutes. Documented here so the next person doesn't re-try the same
> thing.

**What was attempted in `deploy/aspire/Program.cs`:**
```csharp
var identity = builder.AddProject<Projects.Identity_Api>("identity-svc")
    .WaitFor(vault)                      // <- only waits for Vault container
    .WithReference(identityDb)
    .WithEnvironment("Vault__Enabled", "true");
```

**Why it sounded right:** `src/BuildingBlocks/Vault/VaultConfigBootstrap.cs`
already calls `WaitForFileAsync(roleIdPath, TimeSpan.FromSeconds(60), ct)` —
polls every 500ms for the credential files. Surely if the file-poll already
exists, the Aspire-level gate is redundant?

**Why it actually broke:** the file-poll covers *file existence*. It does
NOT cover the other two preconditions for an AppRole login to succeed:

1. ✅ `role_id` + `secret_id` files exist — `vault-init`'s first phase writes them
2. ❌ The matching **AppRole is configured on the Vault server** with policies that grant `secret/identity/*` read — `vault-init`'s second phase
3. ❌ The KV secrets at `secret/identity/*` exist — `vault-seed` writes them

With `WaitFor(vault)`, identity reads the files (which appear quickly),
hands them to `VaultAppRoleAuthenticator.LoginAsync`, and gets HTTP 400
from Vault because the AppRole isn't set up yet. Polly retries a few
times, exhausts, identity crashes with exit code 134, Aspire restarts it,
crash-loops forever.

Verified live: with `WaitFor(vault)`, identity-svc never reached `online`
in 290 seconds of attempts. Reverting to `WaitForCompletion(vaultSeed)`
brings it back in ~30s.

**The lesson:** `WaitForFileAsync` was a partial solution to a different
problem (gracefully handling Aspire startup ordering when files happen to
appear on a delay). It doesn't substitute for "Vault server is fully
configured."

**What WOULD work in the Layer 2 spirit** but wasn't worth shipping:
- Wrap `VaultConfigBootstrap.LoadAsync` in a much longer Polly retry
  (~60s budget) so it tolerates the AppRole not yet existing on the Vault
  server. Identity would then start in ~5s instead of waiting for
  `WaitForCompletion`'s ~30s, and silently retry until Vault is configured.
  Cost: requires changing `BuildingBlocks/Vault/VaultAppRoleAuthenticator.cs`
  to expose retry policy, plus a hosted-service wrapper so the retries
  don't block `Program.Main`. Not worth it when Layer 3 (the proper fix)
  is already designed and pending a real cluster deploy.

---

## 5. Layer 3 — Vault Agent Injector (prod target, designed not deployed)

### How it works in k8s

The Vault Agent Injector is a Kubernetes mutating admission webhook published
by HashiCorp. When a Pod is created with the right annotations, the injector:

1. Adds a `vault-agent-init` initContainer to the Pod
2. Adds a `vault-agent` sidecar container to the Pod
3. Mounts an in-memory volume at `/vault/secrets/` shared between sidecar and app

The init container authenticates to Vault (via the **Kubernetes Auth method** —
the Pod's ServiceAccount JWT is the proof of identity, no `secret_id` to
manage), pulls secrets, writes them as files to the shared volume.

The sidecar runs continuously, refreshing leases before they expire, rewriting
the files on rotation, and optionally signaling the app via SIGHUP or HTTP.

### The Pod annotations look like this

```yaml
metadata:
  annotations:
    vault.hashicorp.com/agent-inject: "true"
    vault.hashicorp.com/role: "identity-svc"
    vault.hashicorp.com/agent-inject-secret-jwt: "secret/data/identity/jwt"
    vault.hashicorp.com/agent-inject-template-jwt: |
      {{- with secret "secret/data/identity/jwt" -}}
      Jwt__SigningKey={{ .Data.data.SigningKey }}
      Jwt__Issuer={{ .Data.data.Issuer }}
      {{- end }}
```

### What's already in our Helm charts

[`deploy/helm/identity-svc/values.yaml`](../../deploy/helm/identity-svc/values.yaml)
already has the `vault.enabled: true` block plus the role + secretPath:
```yaml
vault:
  enabled: true
  role: identity-svc
  secretPath: secret/data/identity
```

[`deploy/helm/identity-svc/templates/deployment.yaml`](../../deploy/helm/identity-svc/templates/deployment.yaml)
emits the Vault Agent Injector annotations conditionally on
`.Values.vault.enabled`. The chart is ready; we just haven't deployed it.

### What changes in the application code

In Layer 3 mode, `VaultConfigBootstrap.LoadAsync` is **not used**. The Vault
Agent has already written secrets as files at `/vault/secrets/<name>`. The
service:
1. Sets `ASPNETCORE_*` config to read from `/vault/secrets/` (e.g. via
   `AddKeyPerFile("/vault/secrets")` from `Microsoft.Extensions.Configuration.KeyPerFile`).
2. No AppRole login at startup. No KV reads. No `Vault:RoleIdPath`.
3. Sidecar handles rotation; app picks up new values on next read because
   `KeyPerFile` watches the directory.

### Migration path when we get to a cluster

1. Deploy Vault to the cluster via the official Helm chart with Kubernetes
   Auth enabled.
2. Bind each service's ServiceAccount to its Vault role
   (`vault write auth/kubernetes/role/identity-svc bound_service_account_names=identity-svc bound_service_account_namespaces=haworks policies=svc-identity ttl=24h`).
3. Configure the Vault Agent Injector helm chart with the Pod-mutation
   webhook.
4. Drop the `Vault__Enabled` env var from the deployment — the existence of
   the injected files at `/vault/secrets/` is the only thing that matters.
5. Add `builder.Configuration.AddKeyPerFile("/vault/secrets")` to every
   service's `Program.cs` that needs Vault secrets. Conditional on file
   existence — local dev path stays as Layer 2.
6. Delete `vault-init.sh`, `vault-seed.sh`, and the `vault-creds/` host bind
   mount. They have no Layer 3 analogue.

The Aspire AppHost still uses Layer 2 for local dev — Aspire isn't k8s, the
Vault Agent Injector is k8s-only. This is fine: prod and dev legitimately
need different secret-delivery mechanisms because their threat models are
different (your laptop isn't a multi-tenant kube cluster).

---

## 6. Decision: when to actually do Layer 3

Layer 3 lands when **at least one** of these is true:
- A real cluster exists for the platform (kind on a VPS, EKS, GKE, etc.)
- Local dev moves to k3s/kind for parity with prod
- The platform handles non-dev secrets (real Stripe keys, real SMTP creds)
  that warrant the auditable rotation that Vault Agent provides

Until then Layer 2 is **honest** — it documents the dev/prod split rather
than pretending dev secret handling matches prod.

---

## 7. Summary

| Era | Mechanism | Identity startup time | Where secrets live |
|---|---|---|---|
| **Now** | **Layer 1: `WaitForCompletion(vaultSeed)`** | ~25-30s (Aspire reconciler bug we live with) | host-bind dir |
| Tried | Layer 2: `WaitFor(vault)` + bootstrap file-poll | crash-loop (see §4) | host-bind dir |
| Future (prod) | Layer 3: Vault Agent Injector | ~5s (cred files mounted before app starts) | in-memory pod volume |

Implementation pointers:
- Layer 2: [`deploy/aspire/Program.cs`](../../deploy/aspire/Program.cs) (the `WaitFor(vault)` line on identity-svc),
  [`src/BuildingBlocks/Vault/VaultConfigBootstrap.cs`](../../src/BuildingBlocks/Vault/VaultConfigBootstrap.cs)
  (the `WaitForFileAsync` polling loop)
- Layer 3 (designed): [`deploy/helm/identity-svc/templates/deployment.yaml`](../../deploy/helm/identity-svc/templates/deployment.yaml)
  (the Vault Agent injection annotations, gated on `.Values.vault.enabled`)
