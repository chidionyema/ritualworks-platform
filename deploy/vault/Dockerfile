# Vault dev-mode wrapper for Fly.io.
#
# Wraps `hashicorp/vault` so the container auto-seeds KV+AppRole on every
# start. Dev-mode storage is in-memory, so a Fly machine restart wipes
# state — the auto-seed makes that self-healing instead of fatal.
#
# What the seed sets up:
#   * KV v2 at secret/  (always present in dev-mode, just confirmed)
#   * AppRole auth method
#   * Policy "haworks-identity-app" (read on secret/, read on database/creds/*)
#   * AppRole role "haworks-identity-app" with that policy
#   * The role's role_id and a fresh secret_id are stashed in KV at
#     secret/identity/bootstrap so the identity service can fetch them
#     at startup using its bootstrap token.
FROM hashicorp/vault:1.18

# curl + jq for the seed script. The base image is alpine.
RUN apk add --no-cache bash curl jq

COPY deploy/vault/vault.hcl     /vault/config/vault.hcl
COPY deploy/vault/entrypoint.sh /usr/local/bin/entrypoint.sh
COPY deploy/vault/seed.sh       /usr/local/bin/seed.sh
RUN chmod +x /usr/local/bin/entrypoint.sh /usr/local/bin/seed.sh \
 && mkdir -p /vault/data \
 && chown -R vault:vault /vault

EXPOSE 8200

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
