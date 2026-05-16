#!/usr/bin/env bash
# One-command Fly.io activation. Idempotent — safe to re-run.
#
#   deploy/fly/up.sh
#
# What it does, in order:
#   1.  Install missing tools (flyctl, gh) via brew, asking first.
#   2.  Walk you through `flyctl auth login` and `gh auth login` if not
#       already authenticated.
#   3.  Prompt for the four required runtime URLs and write them into
#       deploy/fly/.env.local. Secrets are read with -s (no echo) so
#       they never appear in the terminal or shell history.
#   4.  Run bootstrap.sh — creates the seven Fly apps if missing,
#       allocates a public IP only on the BFF, auto-generates the JWT
#       signing key on first run, stages every per-service secret.
#   5.  Run deploy.sh — deploys services in dependency order.
#   6.  Generate a Fly deploy token and pipe it straight into
#       `gh secret set FLY_API_TOKEN` (token never lands in a file or
#       shell history).
#   7.  Print a post-deploy status board and the public URL.

set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_DIR="$ROOT_DIR/deploy/fly"
ENV_FILE="$ENV_DIR/.env.local"
EXAMPLE="$ENV_DIR/.env.example"
REPO="${GITHUB_REPO:-chidionyema/haworks-platform}"

# ─── colors / helpers ─────────────────────────────────────────────────────
bold()   { printf '\033[1m%s\033[0m\n' "$*"; }
ok()     { printf '\033[32m%s\033[0m\n' "$*"; }
warn()   { printf '\033[33m%s\033[0m\n' "$*" >&2; }
fatal()  { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
prompt() { printf '\033[36m%s\033[0m ' "$*"; }

confirm() {
  local q="$1" reply
  prompt "$q [y/N]"
  read -r reply
  [[ "$reply" =~ ^[Yy]$ ]]
}

# Read a value silently (no echo) and append to .env.local. Empty input
# leaves the file unchanged. Doesn't echo the value. Re-prompting is
# safe — we only write when input is non-empty.
read_secret_into_env() {
  local var="$1" hint="$2" current=""
  if [[ -f "$ENV_FILE" ]]; then
    current="$(grep -E "^${var}=" "$ENV_FILE" 2>/dev/null | cut -d= -f2- || true)"
  fi

  # Skip if already set to something real (not the example placeholder).
  if [[ -n "$current" && "$current" != *"USER:PASS"* && "$current" != *"PASS@ep-xxx"* && "$current" != *"TOKEN@host"* ]]; then
    ok "  $var already set, leaving alone"
    return 0
  fi

  prompt "  $var ($hint):"
  local val
  read -rs val
  echo
  if [[ -z "$val" ]]; then
    warn "    blank — leaving $var unset (you can re-run later)"
    return 0
  fi

  if grep -qE "^${var}=" "$ENV_FILE" 2>/dev/null; then
    if [[ "$(uname)" == "Darwin" ]]; then
      sed -i '' "s|^${var}=.*|${var}=${val//|/\\|}|" "$ENV_FILE"
    else
      sed -i "s|^${var}=.*|${var}=${val//|/\\|}|" "$ENV_FILE"
    fi
  else
    printf '%s=%s\n' "$var" "$val" >> "$ENV_FILE"
  fi
  ok "    saved (file is gitignored)"
}

# ─── 1.  Tools ────────────────────────────────────────────────────────────
bold "==> 1/7  Tools"
NEED_BREW_INSTALL=()
command -v flyctl >/dev/null  || NEED_BREW_INSTALL+=(flyctl)
command -v gh     >/dev/null  || NEED_BREW_INSTALL+=(gh)
command -v openssl >/dev/null || fatal "openssl not installed. macOS ships it; install Xcode CLT: xcode-select --install"

if [[ ${#NEED_BREW_INSTALL[@]} -gt 0 ]]; then
  command -v brew >/dev/null || fatal "brew not installed. https://brew.sh — then re-run this script."
  warn "  Missing: ${NEED_BREW_INSTALL[*]}"
  if confirm "  Run 'brew install ${NEED_BREW_INSTALL[*]}'?"; then
    brew install "${NEED_BREW_INSTALL[@]}"
  else
    fatal "  Install required tools and re-run."
  fi
else
  ok "  flyctl, gh, openssl all present"
fi

# ─── 2.  Auth ─────────────────────────────────────────────────────────────
bold "==> 2/7  Auth"
if flyctl auth whoami >/dev/null 2>&1; then
  ok "  flyctl: $(flyctl auth whoami 2>&1)"
else
  warn "  flyctl: not logged in. Opening browser for login..."
  flyctl auth login
fi

if gh auth status >/dev/null 2>&1; then
  ok "  gh:     $(gh auth status 2>&1 | grep -m1 'Logged in' || echo 'authenticated')"
else
  warn "  gh: not logged in. Starting interactive login..."
  gh auth login
fi

# ─── 3.  Runtime config (.env.local) ──────────────────────────────────────
bold "==> 3/7  Runtime config"
if [[ ! -f "$ENV_FILE" ]]; then
  cp "$EXAMPLE" "$ENV_FILE"
  ok "  Created $ENV_FILE from template (gitignored)"
fi

cat <<'PROMPT_HEADER'
  Enter the four required values. Each is read silently — characters
  won't appear as you type or paste. Press Enter on a blank line to
  skip (you can re-run later). Your terminal won't log these.

  Have these ready:
    - CloudAMQP dashboard:  the amqps://... URL
    - Upstash dashboard:    the rediss://... URL
    - Neon dashboard:       the connection string (we split it into
                            base + query below)
PROMPT_HEADER
echo

read_secret_into_env RABBITMQ_URL  "amqps://user:pass@host.cloudamqp.com/vhost"
read_secret_into_env REDIS_URL     "rediss://default:token@host.upstash.io:6379"
read_secret_into_env POSTGRES_BASE "postgres://user:pass@ep-xxx-pooler.region.aws.neon.tech (no /dbname, no ?query)"
read_secret_into_env POSTGRES_QUERY "?sslmode=require&channel_binding=require"

# Validate that the four are now real (not placeholders).
# shellcheck disable=SC1090
set -a; source "$ENV_FILE"; set +a
missing=()
[[ -z "${RABBITMQ_URL:-}"   || "$RABBITMQ_URL"   == amqps://USER:PASS@*       ]] && missing+=(RABBITMQ_URL)
[[ -z "${REDIS_URL:-}"      || "$REDIS_URL"      == rediss://default:TOKEN@*  ]] && missing+=(REDIS_URL)
[[ -z "${POSTGRES_BASE:-}"  || "$POSTGRES_BASE"  == postgres://default:PASS@* ]] && missing+=(POSTGRES_BASE)
[[ -z "${POSTGRES_QUERY:-}"                                                   ]] && missing+=(POSTGRES_QUERY)
if [[ ${#missing[@]} -gt 0 ]]; then
  fatal "  Still missing: ${missing[*]}. Re-run the script and supply them."
fi
ok "  All four runtime values present"

# ─── 4.  Bootstrap (apps + secrets) ───────────────────────────────────────
bold "==> 4/7  Bootstrap (creates apps, stages secrets)"
"$ENV_DIR/bootstrap.sh" "$ENV_FILE"

# ─── 5.  Deploy ───────────────────────────────────────────────────────────
bold "==> 5/7  First deploy (this can take ~10 min on cold image pulls)"
"$ENV_DIR/deploy.sh"

# ─── 6.  GitHub Actions FLY_API_TOKEN ─────────────────────────────────────
bold "==> 6/7  GitHub Actions deploy token"
if gh secret list --repo "$REPO" 2>/dev/null | grep -q '^FLY_API_TOKEN'; then
  ok "  FLY_API_TOKEN already set on $REPO — leaving alone"
  warn "  (re-run with FORCE_ROTATE_TOKEN=1 to rotate)"
  if [[ "${FORCE_ROTATE_TOKEN:-0}" == "1" ]]; then
    warn "  rotating FLY_API_TOKEN per FORCE_ROTATE_TOKEN=1"
    flyctl tokens create deploy --name "github-actions-$(date +%Y%m%d)" --expiry 8760h \
      | gh secret set FLY_API_TOKEN --repo "$REPO"
    ok "  rotated"
  fi
else
  ok "  Creating new deploy token + setting as repo secret..."
  flyctl tokens create deploy --name "github-actions-$(date +%Y%m%d)" --expiry 8760h \
    | gh secret set FLY_API_TOKEN --repo "$REPO"
  ok "  FLY_API_TOKEN set on $REPO"
fi

# ─── 7.  Status board ─────────────────────────────────────────────────────
bold "==> 7/7  Status"
echo
SERVICES=(identity catalog orders payments checkout bffweb)
[[ "${DEPLOY_CONTENT:-false}" == "true" ]] && SERVICES+=(content)

for svc in "${SERVICES[@]}"; do
  app="haworks-$svc"
  status=$(flyctl status -a "$app" --json 2>/dev/null | grep -o '"Status":"[^"]*"' | head -1 | cut -d'"' -f4 || echo "?")
  printf '  %-28s %s\n' "$app" "$status"
done

echo
bold "Public URL:  https://haworks-bffweb.fly.dev"
echo "Logs:        flyctl logs -a haworks-<svc>"
echo "Rollback:    flyctl releases rollback -a haworks-<svc>"
echo "Auto-deploy: every green-CI push to main now lands on Fly via .github/workflows/deploy.yml"
echo
ok "Done."
