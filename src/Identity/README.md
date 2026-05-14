# Identity Service

## Overview

The Identity service is the authentication and authorization boundary for the RitualWorks platform. It owns user registration, credential validation, JWT issuance, refresh token lifecycle, token revocation, external OAuth provider integration (Google, Microsoft, Facebook), and the JWKS endpoint consumed by all downstream services for token validation.

Bounded context: **Identity**. No other service reads or writes the `identity` schema. Other services receive the opaque `UserId` string via JWT claims and maintain their own cross-context data via events.

---

## Architecture

Clean Architecture with four projects:

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `Identity.Domain` | `User`, `UserProfile`, `RefreshToken`, `RevokedToken` entities; repository interfaces |
| Application | `Identity.Application` | MediatR commands/queries, validators (FluentValidation), application interfaces, JWT/security options |
| Infrastructure | `Identity.Infrastructure` | `AppIdentityDbContext` (EF Core + ASP.NET Identity), JWT signing key ring (Vault-backed RSA rotation), repository implementations, token services |
| API | `Identity.Api` | ASP.NET Core controllers, Vault bootstrap, rate limiting, Serilog, migration runner |

**Key dependencies:**
- ASP.NET Core Identity (`IdentityDbContext<User>`) for password hashing, roles, and external login linking
- MediatR for CQRS dispatch
- FluentValidation for command validation
- Vault (`VaultConfigBootstrap`) for RSA signing key material and OAuth client secrets — loaded into `IConfiguration` before DI build (`secret/identity/jwt`, `secret/identity/oauth/{provider}`)
- `Haworks.BuildingBlocks.Vault` for rotating JWT signing key ring (supports grace-window key overlap during rotation)
- Rate limiting: fixed-window, 5 requests/minute per IP on auth endpoints
- OpenTelemetry via `AddServiceDefaults()` (Aspire ServiceDefaults)
- Serilog with explicit console sink

---

## Domain Model

### User (`Identity.Domain.User`)
Extends `IdentityUser` (ASP.NET Core Identity). Additional fields:
- `CheckoutSessionId` — active Stripe checkout session (set by external processes)
- `StripeCustomerId` — Stripe customer reference (indexed, nullable)
- `IsActive` — soft-disable flag
- `CreatedAt`, `UpdatedAt`
- Navigation: `UserProfile` (one-to-one, cascade delete)

### UserProfile (`Identity.Domain.UserProfile`)
Extends `AuditableEntity`. Created via `UserProfile.Create(userId)`. Encapsulates personal and address information with private setters; mutations go through domain methods:
- `UpdatePersonalInfo(firstName, lastName, phone?)`
- `UpdateAddress(address, city, state, postalCode, country)`
- `UpdateProfileInfo(bio?, website?)`
- `SetAvatarUrl(avatarUrl)`
- `RecordLogin()` — stamps `LastLogin`

Default country: `"US"`.

### RefreshToken (`Identity.Domain.RefreshToken`)
Extends `AuditableEntity`. Created via `RefreshToken.Create(userId, token, expires)`. Immutable after creation. Property `IsExpired` checks UTC now vs `Expires`. Default expiry: 7 days (configurable via `JwtOptions`).

### RevokedToken (`Identity.Domain.RevokedToken`)
Extends `AuditableEntity`. Tracks revoked access tokens for the revocation check on logout and refresh. `CanBeCleanedUp` indicates the record has passed its `ExpiresAt` and can be pruned. Token stored with max length 500; reason max 200 chars.

### Invariants
- `UserProfile.UserId` must be non-null/whitespace
- `RefreshToken` token and userId must be non-null/whitespace
- `RevokedToken` token must be non-null/whitespace

---

## API Endpoints

### Authentication (`/api/authentication`)

| Method | Route | Auth | Rate Limited | Description |
|---|---|---|---|---|
| GET | `/api/authentication/csrf-token` | None | No | Issues antiforgery token |
| POST | `/api/authentication/register` | None | Yes (5/min/IP) | Register new user; returns JWT + refresh token |
| POST | `/api/authentication/login` | None | Yes (5/min/IP) | Credential login; returns JWT + refresh token |
| POST | `/api/authentication/logout` | Bearer JWT | No | Revokes current token; requires authentication |
| GET | `/api/authentication/verify-token` | Bearer JWT | No | Validates current JWT, returns claims summary |
| POST | `/api/authentication/refresh-token` | None | Yes (5/min/IP) | Exchange expired access token + refresh token for new pair |

### User Profile (`/api/userprofile`)

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/userprofile` | Bearer JWT | Retrieve profile for authenticated user |
| PUT | `/api/userprofile` | Bearer JWT | Update personal info, address, bio, website |
| POST | `/api/userprofile/shipping-info` | Bearer JWT | Save shipping address fields |

### External Authentication (`/api/external-authentication`)

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/external-authentication/challenge/{provider}` | None | Redirect to OAuth provider (Google/Microsoft/Facebook) |
| GET | `/api/external-authentication/callback` | None | OAuth callback; issues JWT on success |
| GET | `/api/external-authentication/providers` | None | List configured OAuth providers |
| POST | `/api/external-authentication/link/{provider}` | Bearer JWT | Link external provider to existing account |
| GET | `/api/external-authentication/link-callback` | None | OAuth callback for account linking flow |
| DELETE | `/api/external-authentication/unlink/{provider}` | Bearer JWT | Remove external login from account |
| GET | `/api/external-authentication/logins` | Bearer JWT | List all external logins for user |

### JWKS (`/.well-known/jwks.json`)

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/.well-known/jwks.json` | None | RSA JWK Set (RFC 7517) for downstream JWT validation. Returns active key + retiring keys within grace window. |

### Admin (`/admin`)

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/admin/vault/status` | None (internal only) | Live Vault health probe; returns lease TTL for `haworks-identity` role |
| POST | `/admin/vault/rotate-credentials` | None (internal only) | Trigger credential rotation for a Vault AppRole; publishes `VaultRotationStageEvent` per stage |

> **Note:** Admin endpoints are unauthenticated and must be protected by network policy (mesh/localhost-only) before production deployment.

---

## Events

### Published

| Event | Trigger | Contract |
|---|---|---|
| `VaultRotationStageEvent` | `POST /admin/vault/rotate-credentials` — one per stage: `started`, `credentials-fetched`, `applied`, `validated`, `revoked-old` | `Haworks.Contracts.Identity.VaultRotationStageEvent` |

Identity does not publish user registration or login events in the current implementation. Other services receive user identity via JWT claims only.

### Consumed

Identity does not consume any events from the message bus. It is a source-of-truth service.

---

## Configuration

### Required settings

| Key | Source | Description |
|---|---|---|
| `Jwt:Key` | Vault `secret/identity/jwt` or appsettings | HMAC key (used when Vault is disabled) |
| `Jwt:Issuer` | Vault / appsettings | JWT `iss` claim |
| `Jwt:Audience` | Vault / appsettings | JWT `aud` claim |
| `Jwt:TokenExpiryMinutes` | appsettings | Access token lifetime (5–60, default 15) |
| `Jwt:RefreshTokenExpiryDays` | appsettings | Refresh token lifetime (1–90, default 7) |
| `Vault:Enabled` | appsettings | Enable Vault integration (`true`/`false`) |
| `Vault:Address` | appsettings | Vault server URL (required when enabled) |
| `ConnectionStrings:IdentityDb` | appsettings / Vault dynamic creds | PostgreSQL connection string |

### Optional (OAuth providers — loaded from Vault when `Vault:Enabled=true`)

| Key | Vault Path | Description |
|---|---|---|
| `Authentication:Google:ClientId/ClientSecret` | `secret/identity/oauth/google` | Google OAuth 2.0 |
| `Authentication:Microsoft:ClientId/ClientSecret` | `secret/identity/oauth/microsoft` | Microsoft OAuth 2.0 |
| `Authentication:Facebook:AppId/AppSecret` | `secret/identity/oauth/facebook` | Facebook OAuth 2.0 |

Providers with blank `ClientId` are skipped during DI registration — missing KV paths do not fail startup.

### Security options

`SecurityOptions` (bound from appsettings): `AllowedRedirectHosts` — list of hosts permitted as OAuth redirect targets.

---

## Database

- **Schema:** `identity`
- **DbContext:** `AppIdentityDbContext` extends `IdentityDbContext<User>`
- **Migration runner:** `MigrateWithRetryAsync` on startup (skipped in `Test` environment)
- **Role seeding:** Roles `Admin`, `ContentUploader`, `User` are seeded on startup

### Key tables

| Table | Description |
|---|---|
| `identity.AspNetUsers` | ASP.NET Identity users (+ `CheckoutSessionId`, `StripeCustomerId`, `IsActive`) |
| `identity.UserProfiles` | Personal info, address, bio; 1:1 with Users (cascade delete) |
| `identity.RefreshTokens` | Active refresh tokens; indexed on `Token`, `UserId`, `Expires` |
| `identity.RevokedTokens` | Revoked access tokens; indexed on `Token`, `RevokedAt`, `ExpiresAt` |
| `identity.AspNetRoles` | Roles (Admin, ContentUploader, User) |
| `identity.AspNetUserRoles` | User-role join |
| `identity.AspNetUserLogins` | External provider account links |

### Migrations

| Migration | Date | Description |
|---|---|---|
| `20260503105929_InitialCreate` | 2026-05-03 | Full schema: users, profiles, tokens, ASP.NET Identity tables |

### Concurrency

Auditable entities carry `CreatedAt`, `CreatedBy`, `CreatedFromIp`, `LastModifiedDate`, `LastModifiedBy`, `ModifiedFromIp` stamped by `SaveChangesAsync`.

---

## Testing

### Test projects

| Project | Location | Coverage |
|---|---|---|
| `Identity.Unit` | `tests/Identity/Identity.Unit/` | Command handlers (Register, Login, SaveShippingInfo, UpdateUserProfile), query handlers (GetUserProfile, VerifyToken, GetAvailableProviders), validators (Login, Register, RefreshToken, user commands), controllers, JwtTokenService, RefreshTokenService, TokenRevocationService, UserEmailService, domain model |
| `Identity.Integration` | `tests/Identity/Identity.Integration/` | Full HTTP flow tests (AuthFlowsTests, RoundedOutAuthFlowsTests) via `IdentityWebAppFactory` (WebApplicationFactory) |
| `Identity.Architecture` | `tests/Identity/Identity.Architecture/` | Dependency boundary enforcement (no Infrastructure references from Domain/Application) |
| `Identity.Contract` | `tests/Identity/Identity.Contract/` | Pact contract tests for event consumers |

### Running tests

```bash
# Unit tests
dotnet test tests/Identity/Identity.Unit/

# Integration tests (requires Docker for PostgreSQL via shared Testcontainers)
dotnet test tests/Identity/Identity.Integration/

# Architecture tests
dotnet test tests/Identity/Identity.Architecture/
```

Integration tests use `SharedTestPostgres.CreateDatabaseAsync("identity")` from `BuildingBlocks.Testing.Containers`. Do not create raw `PostgreSqlBuilder` instances — the CI architecture check enforces this.
