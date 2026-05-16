# Deploying to Fly.io

End-to-end deploy for the seven haworks microservices on Fly.io. The
local Aspire AppHost (`deploy/aspire/`) is unaffected by anything here —
it keeps working for local dev. This directory only covers Fly.

## TL;DR

> **Before running the wizard, you must already have accounts and resources
> at three managed-service providers.** The wizard prompts for their URLs;
> if you don't have them ready it'll exit with no progress.

**Pre-flight checklist (do these first, ~10 min):**

| Provider | Sign up at | What you create | What you grab |
|---|---|---|---|
| **Neon** (Postgres) | [neon.tech](https://neon.tech) | One project, six databases inside it (`identity`, `catalog`, `orders`, `payments`, `checkout`, `content`) | Connection string base (one for the project — DB-name segment differs per service) |
| **CloudAMQP** (RabbitMQ) | [cloudamqp.com](https://cloudamqp.com) | One instance (Little Lemur free tier is fine) | AMQPS URL |
| **Upstash** (Redis) | [upstash.com](https://upstash.com) | One database | `rediss://` URL |

Pick regions close to Fly `iad` (US East): Neon + Upstash `us-east-1`,
CloudAMQP US East. EU regions add ~70ms per round-trip.

You also need a Fly.io account and the GitHub repo cloned locally. The
wizard handles both Fly + GitHub browser logins for you.

**Then deploy:**

```bash
deploy/fly/up.sh
```

That's the whole deploy. The wizard installs missing tools (`flyctl`,
`gh`) on demand, walks you through Fly + GitHub auth, prompts for the
four URLs from the table above, creates the seven apps, deploys, and
wires `FLY_API_TOKEN` into GitHub Actions so every subsequent push to
`main` auto-deploys. Idempotent — re-run any time. Read the rest if you
want to understand what it's doing or override pieces.

## What runs where

| Service | Fly app | Public? | Reachable at |
|---|---|---|---|
| BFF | `haworks-bffweb` | yes | `https://haworks-bffweb.fly.dev` |
| Identity | `haworks-identity` | no | `http://haworks-identity.flycast:8080` |
| Catalog | `haworks-catalog` | no | `http://haworks-catalog.flycast:8080` |
| Orders | `haworks-orders` | no | `http://haworks-orders.flycast:8080` |
| Payments | `haworks-payments` | no | `http://haworks-payments.flycast:8080` |
| Checkout | `haworks-checkout` | no | `http://haworks-checkout.flycast:8080` |
| Content | `haworks-content` | no, opt-in | `http://haworks-content.flycast:8080` |

Backends are private — only the BFF gets a public IP. Inter-service traffic
goes over Fly's flycast (private 6PN with load balancing).

## Prerequisites

1. **macOS with Homebrew, OR Linux with `brew` available.** `up.sh` will
   install missing tools (`flyctl`, `gh`) on demand.
2. **A Fly.io account** and **a GitHub account that owns this repo**.
   The wizard walks you through both browser logins.
3. **Three managed services** signed up. The wizard reads URLs from these:

| Provider | What you grab |
|---|---|
| **Neon** (Postgres) | One project; create 6 databases inside it (`identity`, `catalog`, `orders`, `payments`, `checkout`, `content`). Connection string from the dashboard — only the database-name segment differs per service. |
| **CloudAMQP** (RabbitMQ) | One instance; copy the AMQPS URL. |
| **Upstash** (Redis) | One database; copy the `rediss://` URL. |

Pick regions close to Fly `iad` (US East) — Neon `us-east-1`, Upstash
`us-east-1`, CloudAMQP region of choice. CloudAMQP in `eu-west-1` adds
~70ms per AMQP round-trip from `iad`.

## First deploy — one command

```bash
cd haworks-platform
deploy/fly/up.sh
```

That's it. The wizard is idempotent — re-run safely. What it does:

1. **Tools** — detects missing `flyctl` / `gh`; asks before brew-installing.
2. **Auth** — detects unauthenticated state; runs `flyctl auth login` and
   `gh auth login` as needed (browser flows).
3. **Prompts for the four required runtime URLs** with `read -rs` (silent
   input — characters never echo to the terminal). Values land in
   `deploy/fly/.env.local` (gitignored). Already-filled values are left
   alone, so partial re-runs are fine.
4. **Bootstrap** — auto-generates an RSA-2048 JWT signing key, creates the
   seven Fly apps, allocates a public IP only on `haworks-bffweb`,
   stages every per-service secret via `flyctl secrets import --stage`.
5. **First deploy** — `identity` first (others auth against it), then
   `catalog`/`orders`/`payments`/`checkout` in parallel, then BFF last.
   Cold first run takes ~10 minutes (image pulls).
6. **GitHub Actions deploy token** — generates a Fly deploy token and
   pipes it directly into `gh secret set FLY_API_TOKEN`. The token never
   lands on disk or in shell history. Skips this step if the secret is
   already set; rotate via `FORCE_ROTATE_TOKEN=1 deploy/fly/up.sh`.
7. **Status board** — prints per-app `Status:`, the public URL, and a
   confirmation that auto-deploy is now live.

After this, every green-CI push to `main` triggers
`.github/workflows/deploy.yml` and lands on Fly automatically.

## What lands in `.env.local`

The four values the wizard prompts for. Plus auto-generated and optional
fields. **Never commit this file** — it's gitignored via the existing
`.env.*` rule.

```
RABBITMQ_URL=amqps://...                   # wizard prompts
REDIS_URL=rediss://...                     # wizard prompts
POSTGRES_BASE=postgres://...               # wizard prompts (no /dbname, no ?query)
POSTGRES_QUERY=?sslmode=require&...        # wizard prompts

JWT_SIGNING_KEY_PEM=<base64 RSA PEM>       # auto-generated on first bootstrap
JWT_KEY_ID=fly-1                           # bump if you rotate the key

# Optional — leave blank if unused. Edit the file directly to add these.
OAUTH_GOOGLE_CLIENT_ID=
OAUTH_GOOGLE_CLIENT_SECRET=
# (microsoft, facebook similar)
STRIPE_WEBHOOK_SECRET=
# Tigris block — only if DEPLOY_CONTENT=true (see "Adding Content")
DEPLOY_CONTENT=false
```

To add an optional value later: edit `.env.local`, re-run `up.sh`. The
wizard restages affected secrets onto Fly.

## Subsequent deploys

After the wizard has activated CD once, just push to `main`:

```bash
git push origin main
```

`.github/workflows/ci.yml` runs unit + integration tests; on green,
`.github/workflows/deploy.yml` fires automatically and deploys to Fly.

Manual deploy options if you need them:

```bash
deploy/fly/deploy.sh                            # all services in dependency order
flyctl deploy -c fly.<svc>.toml --remote-only   # one specific service
gh workflow run "Deploy" --repo chidionyema/haworks-platform
                                                # trigger the GH workflow manually
```

## Rotating secrets

**Runtime credentials** (RabbitMQ, Redis, Postgres, JWT, OAuth, Stripe).
Edit `.env.local` and re-run `up.sh`. The bootstrap step restages every
secret onto Fly via `flyctl secrets import --stage`; deploy applies them.

**Single Fly secret without going through `.env.local`** (e.g. one-off
overrides):

```bash
flyctl secrets set -a haworks-payments \
  Webhooks__Stripe__WebhookSecret='whsec_...'
```

**The GitHub Actions deploy token (`FLY_API_TOKEN`).** Tokens have an
8760h (1y) expiry by default. To rotate:

```bash
FORCE_ROTATE_TOKEN=1 deploy/fly/up.sh
```

The wizard generates a new token, pipes it into `gh secret set`, and
discards the old one. The new token is never visible to you.

## Rollback

Per-service:

```bash
flyctl releases list -a haworks-<svc>
flyctl releases rollback -a haworks-<svc>
```

Cross-service rollback isn't automated — roll back each app individually.

## Adding Content service

Default skips `haworks-content` because it needs S3-compatible storage.
To opt in with Fly Tigris:

```bash
# 1. Create the app first so storage attaches to it.
flyctl apps create haworks-content

# 2. Provision Tigris. flyctl prints the credentials to stdout.
flyctl storage create -a haworks-content

# 3. Copy the printed AWS_* values into .env.local's TIGRIS_* slots:
#    AWS_ACCESS_KEY_ID         → TIGRIS_ACCESS_KEY
#    AWS_SECRET_ACCESS_KEY     → TIGRIS_SECRET_KEY
#    AWS_ENDPOINT_URL_S3       → TIGRIS_SERVICE_URL  (keep https://)
#    AWS_REGION                → TIGRIS_REGION       (typically "auto")
#    BUCKET_NAME               → TIGRIS_BUCKET
# 4. Set DEPLOY_CONTENT=true in .env.local.

deploy/fly/up.sh             # re-runs end to end with content
```

ClamAV (`CLAMAV_REST_URL`) is optional. Without it, uploads succeed without
virus scanning — fine for a portfolio demo, not for real user uploads.

## Stripe webhook

After payments-svc is up, register the webhook in the Stripe dashboard:

```
URL:    https://haworks-bffweb.fly.dev/api/payments/webhook
Events: payment_intent.succeeded, payment_intent.payment_failed
        checkout.session.completed
```

Copy the signing secret Stripe gives you and either re-run `bootstrap.sh`
with `STRIPE_WEBHOOK_SECRET=whsec_...` in `.env.local`, or one-shot:

```bash
flyctl secrets set -a haworks-payments \
  Webhooks__Stripe__WebhookSecret='whsec_...'
```

## Troubleshooting

**Identity crashloops with "Jwt:SigningKeyPem is required when Vault:Enabled=false".**
The bootstrap didn't generate the key, or the env file was edited and the
key field cleared. Re-run `up.sh` — it auto-generates if blank.

**A backend service crashloops with "ConnectionStrings:rabbitmq is missing".**
Bootstrap failed for that app. Run `flyctl secrets list -a haworks-<svc>`
to confirm. Re-run `up.sh` — it restages secrets that aren't already set.

**`up.sh` says "FLY_API_TOKEN already set" but I want to rotate it.**
`FORCE_ROTATE_TOKEN=1 deploy/fly/up.sh`.

**`up.sh` exits at "Auth" — `gh auth login` complains about no browser.**
You're on a remote shell or headless box. Run `gh auth login --web` and
follow the device-code flow, or generate a personal-access token with
`workflow` scope and paste it via `gh auth login --with-token`.

**The wizard prompted for `RABBITMQ_URL` even though I set it in
`.env.local` directly.** It's checking against the placeholder pattern in
`.env.example`. If your URL contains `USER:PASS@` literally (an example),
edit it. Otherwise the prompt accepts what you already saved when it
detects a non-placeholder value.

**Identity boots but `/api/external-authentication/google-callback` returns 404.**
You didn't supply `OAUTH_GOOGLE_CLIENT_ID`. Conditional registration means
the route is only mapped when credentials are present. Add them and redeploy.

**BFF returns 502 from `/hubs/checkout` or other backend calls.**
Backend service is asleep (auto-stop), or the `Services__<svc>__http__0`
override doesn't match a real flycast hostname. Verify with:
```bash
flyctl status -a haworks-<svc>
flyctl secrets list -a haworks-bffweb | grep Services__
```

**EF migration fails on first deploy.** Neon's `default` role has owner
privileges, so DDL should succeed. If it fails, check the migration
dependency order — services migrate in parallel and only their own DBs.
Manual fix: `flyctl ssh console -a haworks-<svc>` then run the
migration command directly.

**`flyctl deploy` build errors on missing `Directory.Build.props`.** The
Dockerfile build context must be the repo root. The `fly.<svc>.toml` files
already point at `src/<Service>/<Service>.Api/Dockerfile` with build
context = repo root. If you copy a Dockerfile out, keep the path relative
to the repo root.

## Local dev still uses Aspire

`dotnet run --project deploy/aspire` is unchanged. The same code paths that
work on Fly (Vault disabled, `Jwt:SigningKeyPem` from config) are exercised
in dev when `Vault:Enabled=true` flips to false — but the AppHost still
defaults to Vault-enabled, so local dev keeps working as before. Nothing
to change.

## Files in this directory

```
.env.example       — template for .env.local
.env.local         — your filled-in secrets (gitignored, never committed)
bootstrap.sh       — creates apps, stages secrets, generates JWT key
deploy.sh          — deploys all services in dependency order
up.sh              — the wizard: tools, auth, prompts, bootstrap, deploy,
                     GitHub-secret wiring, status. Run this.
README.md          — this file
```

## Cheatsheet

| I want to… | Run |
|---|---|
| Activate Fly + CD from scratch | `deploy/fly/up.sh` |
| Re-deploy after a code change (no infra change) | `git push origin main` |
| Change a runtime secret | edit `.env.local`, re-run `up.sh` |
| Rotate the GitHub Actions deploy token | `FORCE_ROTATE_TOKEN=1 deploy/fly/up.sh` |
| Deploy a single service manually | `flyctl deploy -c fly.<svc>.toml --remote-only` |
| Roll back one service | `flyctl releases rollback -a haworks-<svc>` |
| Tail logs | `flyctl logs -a haworks-<svc>` |
| SSH into a running machine | `flyctl ssh console -a haworks-<svc>` |
| See deploy status of all apps | `for s in bffweb identity catalog orders payments checkout; do flyctl status -a haworks-$s --json \| grep Status; done` |
