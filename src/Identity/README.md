# Identity Service

Authentication and identity management. Issues RS256 JWTs with rotating signing keys, supports external OAuth providers, and processes GDPR erasure requests.

## Responsibilities
- Register, login, logout, token refresh with JTI revocation
- External OAuth: Google, Microsoft, Facebook (challenge / callback / link / unlink)
- Rotate JWT signing keypair via `RotatingJwtSigningKeyRing` (Vault) or static config
- Handle `PrivacyErasureRequestedEvent` — anonymise user records

## API Endpoints
| Method | Route | Notes |
|--------|-------|-------|
| POST | `/api/auth/register` | |
| POST | `/api/auth/login` | |
| POST | `/api/auth/logout` | |
| POST | `/api/auth/verify-token` | |
| POST | `/api/auth/refresh-token` | |
| GET | `/api/auth/external/{provider}` | OAuth challenge |
| GET | `/api/auth/external/{provider}/callback` | OAuth callback |
| POST | `/api/auth/external/link` | Link external account |
| DELETE | `/api/auth/external/unlink` | Unlink external account |

## Domain Entities
- **User** (extends `IdentityUser`) — `StripeCustomerId`, `CheckoutSessionId`, `IsActive`, `Profile`

## Events Consumed
- `PrivacyErasureRequestedEvent`

## Events Published
- `UserRegisteredEvent`

## Infrastructure Dependencies
- PostgreSQL (`IdentityDbContext`) via ASP.NET Identity
- RabbitMQ via MassTransit
- Vault (JWT signing key rotation, optional)

## Configuration
```
ConnectionStrings:identity
Vault:Enabled / RoleId / SecretId
Jwt:Issuer / Audience / (StaticSigningKey if Vault disabled)
Authentication:Google / Microsoft / Facebook — ClientId / ClientSecret
RabbitMq:Host / Username / Password
```

## Health Checks
- Default ASP.NET health endpoint
