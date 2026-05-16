# Vault prod-mode config. Replaces `vault server -dev` so state persists
# across container restarts. Reachable on Fly's 6PN at
# http://haworks-vault.internal:8200; not exposed publicly.

storage "raft" {
  path    = "/vault/data"
  node_id = "haworks-vault-1"
}

# Bind to IPv6 wildcard so the .internal DNS (IPv6-only) can reach us.
# TLS disabled because we're inside Fly's private 6PN — equivalent to a
# Kubernetes pod-to-pod cluster network. Adding TLS here would require
# per-deploy cert rotation that has nothing to do with the demo.
listener "tcp" {
  address     = "[::]:8200"
  tls_disable = "true"
}

api_addr      = "http://[::1]:8200"
cluster_addr  = "http://[::1]:8201"
ui            = false

# mlock keeps secrets from being swapped to disk; on Fly's container
# environment it's not available + would prevent vault from starting.
disable_mlock = true
