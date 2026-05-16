# Vault on Fly (prod-mode, raft on a Fly volume, fully push-driven)

HashiCorp Vault runs inside the cluster's 6PN as a stateful service —
raft storage on a persistent volume, real `vault operator init`, single
unseal key auto-applied on every restart. Powers the
`/api/demo/vault/status` and `/api/demo/vault/rotate` demos AND the
JWT signing-key rotation that flows through every other service.

## Operator workflow

**One-time setup** (after cloning the repo):

```bash
cp deploy/fly/.env.example deploy/fly/.env.local
# fill in RABBITMQ_URL, REDIS_URL, POSTGRES_BASE
./deploy/fly/bootstrap.sh
```

That creates all the Fly apps + volumes (vault, vault-pg, identity,
catalog, …) and stages the non-vault secrets. After this, **everything
runs on `git push`**.

**Steady state** (every change after first-time setup):

```bash
git push   # main triggers .github/workflows/deploy.yml
```

The deploy chain handles vault init, key capture, AppRole staging, and
the full backend roll-out in one workflow run.

## Deploy chain (`.github/workflows/deploy.yml`)

```
plan ─┬─ deploy-vault-pg ─┐
      └─ deploy-vault ─── stage-vault-creds ─── deploy-backends (matrix) ─── deploy-bff
```

* **`deploy-vault-pg`** — first deploy stands up the sandboxed Postgres
  on its own volume; subsequent deploys are no-ops or rolling updates.
* **`deploy-vault`** — if the volume is empty, vault's entrypoint runs
  `vault operator init`, persists `/vault/data/.init.json`, unseals
  itself from the keys it just generated, and runs `seed.sh` (AppRole
  + policy + KV + database secrets engine).
* **`stage-vault-creds`** — CI-side capture step. SSHes into vault,
  reads `.init.json` (only present once after first init), stages
  `VAULT_UNSEAL_KEY` + `VAULT_ROOT_TOKEN_PROD` as Fly secrets so future
  vault restarts auto-unseal without operator action. Then reads the
  `haworks-identity-app` AppRole's `role_id` and writes a fresh
  `secret_id`; stages both on identity. Idempotent — safe to re-run.
* **`deploy-backends`** — rolls identity, catalog, orders, payments,
  checkout in parallel. Identity boots with the just-staged direct
  Vault creds (no boot-time round-trip to vault, no race condition).
* **`deploy-bff`** — final.

If the capture step ever breaks (e.g., transient SSH failure) and you
just need to push backends without re-capturing, dispatch the workflow
manually with `bypass_vault_capture: true`.

## What's seeded

* AppRole auth method
* Policy `haworks-identity-app` (read on `secret/*`, read on
  `database/creds/*`, etc.)
* AppRole role `haworks-identity-app`
* KV v2 entries: `secret/identity/jwt`, `secret/identity/oauth/{google,
  microsoft, facebook}` (placeholders — overwrite via the CLI for real
  OAuth)
* Database secrets engine + `haworks-identity` role pointing at
  `haworks-vault-pg.internal:5432` (only when
  `VAULT_PG_OPERATOR_PASSWORD` is staged — `bootstrap.sh` does that)

## Verification

```bash
# Vault is up + AppRole auth works:
curl -s https://haworks-bffweb.fly.dev/api/demo/vault/status | jq

# JWKS endpoint returns the active key ring:
curl -s https://haworks-bffweb.fly.dev/api/identity/.well-known/jwks.json | jq

# Trigger a rotation (returns 200 immediately, stages stream via SignalR):
curl -s -X POST 'https://haworks-bffweb.fly.dev/api/demo/vault/rotate?roleName=haworks-identity'
```

## Recovery

The unseal key + root token live in two places:

1. `deploy/fly/.env.local` (gitignored) — captured by an earlier
   `bootstrap.sh` run. Lose this and you lose the only off-Fly copy.
2. `Fly secrets on haworks-vault` — staged from `.env.local` /
   captured by CI. If the Fly app is deleted, only `.env.local` is left.

For a portfolio demo this is fine. For real prod you'd back up the
unseal key to 1Password/AWS KMS and use Shamir splitting (key_shares=5,
key_threshold=3) instead of the single-key shortcut.

## Caveats

* **Single-node raft** — no HA. A vault machine OOM-kill briefly takes
  the rotation demo offline (but identity stays up because direct creds
  are staged on it).
* **TLS-disabled listener** — fine for 6PN-internal traffic, never expose
  vault publicly with this config.
