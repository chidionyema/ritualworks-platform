# Vault Agent sidecar configuration
# Runs alongside each service, renders secrets to /vault/secrets/
# Services read via IConfiguration (AddVaultAgentSecrets) — zero SDK needed.
#
# K8s: Deployed via vault.hashicorp.com/agent-inject annotations
# Fly: Run as process in same Machine (fly.toml [processes])
# Docker: Sidecar container in docker-compose

pid_file = "/tmp/vault-agent.pid"

vault {
  address = "${VAULT_ADDR}"
  retry {
    num_retries = 5
  }
}

auto_auth {
  method "approle" {
    config = {
      role_id_file_path   = "/vault/config/role-id"
      secret_id_file_path = "/vault/config/secret-id"
      remove_secret_id_file_after_reading = false
    }
  }

  sink "file" {
    config = {
      path = "/tmp/vault-agent-token"
    }
  }
}

# Template: Database connection string (rotated by Vault DB engine)
template {
  source      = "/vault/templates/db-connection.ctmpl"
  destination = "/vault/secrets/db.env"
  perms       = "0600"
  command     = "echo 'DB credentials rotated'"
}

# Template: Database credentials as JSON (for AgentFile mode)
# Replace SERVICE_ROLE with the actual role name (e.g., haworks-orders)
# and SERVICE_NAME with the service suffix (e.g., orders).
template {
  source      = "/vault/templates/db-creds.ctmpl"
  destination = "/vault/secrets/db-SERVICE_NAME.json"
  perms       = "0600"
  command     = "echo 'DB credentials rotated (JSON)'"
}

# Template: JWT signing key (from Vault KV)
template {
  source      = "/vault/templates/jwt-key.ctmpl"
  destination = "/vault/secrets/jwt.env"
  perms       = "0600"
  command     = "echo 'JWT key refreshed'"
}

# Template: Provider credentials (Stripe, PayPal, etc.)
template {
  source      = "/vault/templates/providers.ctmpl"
  destination = "/vault/secrets/providers.json"
  perms       = "0600"
  command     = "echo 'Provider credentials refreshed'"
}
