# Vault Agent Sidecar

Secrets are injected by Vault Agent running alongside each service — **no Vault SDK in application code**.

## Architecture

```
┌─────────────────────────────────────────┐
│ Pod / Fly Machine                       │
│                                         │
│  ┌──────────────┐   ┌───────────────┐  │
│  │ Vault Agent  │──▶│ /vault/secrets │  │
│  │ (sidecar)    │   │  db.env       │  │
│  │              │   │  jwt.env      │  │
│  │ • auto_auth  │   │  providers.json│  │
│  │ • template   │   └───────┬───────┘  │
│  └──────────────┘           │           │
│                             ▼           │
│  ┌──────────────────────────────────┐   │
│  │ .NET Service                      │   │
│  │ • AddVaultAgentSecrets()          │   │
│  │ • Reads IConfiguration normally   │   │
│  │ • Zero Vault SDK dependency       │   │
│  └──────────────────────────────────┘   │
└─────────────────────────────────────────┘
```

## How it works

1. **Vault Agent** authenticates via AppRole (role-id + secret-id files)
2. **Templates** render secrets from Vault to `/vault/secrets/` as `.env` or `.json`
3. **Service** calls `builder.AddVaultAgentSecrets()` which adds those files to `IConfiguration`
4. **Rotation**: Agent re-renders on TTL expiry; `reloadOnChange: true` picks up new values
5. **File watcher** logs rotation events for observability

## Environments

### K8s (production)

Add annotations to Deployment:

```yaml
annotations:
  vault.hashicorp.com/agent-inject: "true"
  vault.hashicorp.com/role: "payments-svc"
  vault.hashicorp.com/agent-inject-secret-db.env: "database/creds/payments-role"
  vault.hashicorp.com/agent-inject-template-db.env: |
    {{- with secret "database/creds/payments-role" -}}
    ConnectionStrings__DefaultConnection=Host=...;Username={{ .Data.username }};Password={{ .Data.password }}
    {{- end -}}
```

### Fly.io

Run Vault Agent as a process in the same Machine:

```toml
# fly.payments.toml
[processes]
  app = "dotnet Payments.Api.dll"
  vault-agent = "vault agent -config=/vault/config/agent-config.hcl"
```

### Local dev (docker-compose)

No sidecar needed — secrets come from `.env.local` or Aspire env vars.
`AddVaultAgentSecrets()` silently skips if `/vault/secrets/` doesn't exist.

## Migration from in-app Vault SDK

The legacy `AddVaultIntegration()` / `IVaultService` approach is being phased out.
New services should use `AddVaultAgentSecrets()` only. Existing services work either way:

| Approach | When to use |
|----------|-------------|
| `AddVaultAgentSecrets()` | New services, K8s deployments, Fly with agent process |
| `AddVaultIntegration()` | Legacy — still works but adds complexity |
| Neither (static config) | Local dev, tests, Fly without Vault |

## Files

```
infra/vault-agent/
├── agent-config.hcl        # Vault Agent main config
├── templates/
│   ├── db-connection.ctmpl # DB creds → connection string
│   ├── jwt-key.ctmpl       # JWT signing key from KV
│   └── providers.ctmpl     # Stripe/PayPal/SES credentials
└── README.md               # This file
```
