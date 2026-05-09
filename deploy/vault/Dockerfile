# Vault prod-mode wrapper for Fly.io.
#
# Wraps `hashicorp/vault` so the container auto-unseals + seeds the
# per-service AppRole + policy + DB-role topology on every start. State
# lives on the Fly volume mounted at /vault/data (raft storage), so init
# keys + KV values + audit log all persist across restarts.
#
# Seed config is sourced from infra/vault/ (mounted at /vault/manifests/),
# the SAME declarative manifests deploy/aspire/vault-init.sh consumes
# locally. One source of truth, dev and prod.
FROM hashicorp/vault:1.18

# curl + jq for the seed script. The base image is alpine.
RUN apk add --no-cache bash curl jq

COPY deploy/vault/vault.hcl     /vault/config/vault.hcl
COPY deploy/vault/entrypoint.sh /usr/local/bin/entrypoint.sh
COPY deploy/vault/seed.sh       /usr/local/bin/seed.sh
COPY infra/vault/               /vault/manifests/
RUN chmod +x /usr/local/bin/entrypoint.sh /usr/local/bin/seed.sh \
 && mkdir -p /vault/data \
 && chown -R vault:vault /vault

EXPOSE 8200

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
