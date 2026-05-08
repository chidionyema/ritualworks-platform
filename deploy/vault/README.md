# Vault on Fly (dev-mode, auto-reseed, fully automated)

Stands up a HashiCorp Vault server inside the cluster's 6PN so the
`/api/demo/vault/status` and `/api/demo/vault/rotate` demos light up.

**Mode:** Vault dev-mode — in-memory storage, auto-unsealed, single
machine. State is wiped on restart, but `entrypoint.sh` re-runs `seed.sh`
on every boot so the demo self-heals within ~30s of any restart.

## How the deploy chain works

Vault is part of the standard CI/CD flow — there is no manual step.

**One-time setup** (developer machine, after cloning):

```bash
cp deploy/fly/.env.example deploy/fly/.env.local
# fill in RABBITMQ_URL, REDIS_URL, POSTGRES_BASE
./deploy/fly/bootstrap.sh
```

`bootstrap.sh` is idempotent. It:
* creates the `ritualworks-vault` Fly app (alongside the rest)
* auto-generates a `VAULT_DEV_ROOT_TOKEN` and persists it to `.env.local`
* stages it on `ritualworks-vault` as `VAULT_DEV_ROOT_TOKEN_ID`
* stages the same value on `ritualworks-identity` as `VAULT_ROOT_TOKEN`
* stages all the `Vault__*` config on identity (Address, RoleIdPath,
  SecretIdPath, RequireHmacValidation=false)

**Every push to main**:

`.github/workflows/deploy.yml` runs `flyctl deploy -c fly.vault.toml`
**before** `deploy-backends`, so identity always finds Vault reachable
when its bootstrap shim runs. If Vault is genuinely down for >30s on
identity startup, the shim **fails open** — identity boots with
`Vault__Enabled=false`, the demo degrades, but auth itself stays up.

## What the seed sets up

* KV v2 at `secret/`
* AppRole auth method enabled
* Policy `haworks-identity-app` (read on `secret/*` and `database/creds/*`)
* AppRole role `haworks-identity-app` bound to that policy
* `secret/identity/bootstrap` containing `role_id` + `secret_id` for
  identity to fetch on boot
* `secret/identity/jwt` placeholder

## Identity's bootstrap shim (how it actually works)

`deploy/identity/vault-bootstrap.sh` runs as the identity container's
ENTRYPOINT. It:

1. Waits up to 30s for `$Vault__Address/v1/sys/health` to respond.
2. Reads `secret/identity/bootstrap` using `$VAULT_ROOT_TOKEN`.
3. Writes `role_id` to `/tmp/vault/role_id` and `secret_id` to
   `/tmp/vault/secret_id` (chmod 600).
4. Unsets `VAULT_ROOT_TOKEN` from the env.
5. `exec`s `dotnet Identity.Api.dll`.

The .NET app then auths to Vault via AppRole using files on disk —
the standard production-grade path, identical to how it would behave
against a real Vault cluster.

## Verifying the demo end-to-end

After `flyctl deploy` completes for both vault and identity:

```bash
curl -s https://ritualworks-bffweb.fly.dev/api/demo/vault/status | jq
# expected: 200, vault reachable=true
```

The frontend's `LabAutoRunners` and `LabBackgroundProber` both hit
`/api/demo/vault/status` automatically — once Vault is up, the lab
page's "Vault status" runner card flips green within a few seconds.

## Caveats (dev-mode)

* **State is in-memory.** A Vault machine restart wipes everything;
  `seed.sh` rebuilds the structural setup but any **runtime-written**
  values (e.g. KV writes from the rotate endpoint) are lost.
* **Same root token across deploys** (deterministic per `.env.local`).
  Fine for a portfolio demo on private 6PN — never use this pattern in
  real prod.
* **No DB secrets engine.** The Postgres database secrets engine is
  not configured because there's no shared Postgres in the cluster to
  point it at. `/api/demo/vault/rotate` will succeed at the Vault auth
  layer but the rotation itself will 404 from Vault until a
  `database/config/...` is added.

## Upgrade path to "real prod"

Replace dev-mode with `vault server -config=...` backed by a Fly volume:

* Fly volume + `storage "raft"` for persistent state
* Initialize once with `vault operator init` and store the unseal keys
  out of band (1Password, sealed Kubernetes secrets, AWS KMS)
* Auto-unseal via Fly's `[mounts]` + a startup script that pulls the
  unseal key from a separate machine or KMS

That's a ~1-day job and only worth it if the demo grows beyond
"prove the integration works".
