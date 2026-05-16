# R2 — VaultCredentialProvider + Pool Drain

**Brief:** R2 | **Spec:** `docs/agent-briefs/secret-rotation-spec.md`
**Phase:** 2 (parallel with R3 — both require R1 complete)
**Time budget:** 30 min

---

## Inputs

Read before touching anything:
- `src/Scheduler/Scheduler.Infrastructure/DependencyInjection.cs` — Hangfire + Npgsql pattern
- `infra/vault/database/roles.json` — static role names
- `infra/vault/services.json` — service names and `has_db` flags

---

## Deliverable

Create `src/BuildingBlocks/BuildingBlocks.Vault/` (new project):

### BuildingBlocks.Vault.csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="VaultSharp" Version="1.17.*" />
    <PackageReference Include="Npgsql" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.*" />
  </ItemGroup>
</Project>
```

### IVaultCredentialProvider.cs
```csharp
public interface IVaultCredentialProvider
{
    Task<(string Username, string Password)> GetDatabaseCredentialsAsync(
        string roleName, CancellationToken ct);
}
```

### VaultCredentialProvider.cs
- Calls `vault/v1/database/static-creds/{roleName}` via `VaultSharp`.
- Caches result in memory with a `DateTimeOffset` expiry = `rotation_period * 0.9`.
- On cache miss: fetch from Vault, update cache.
- On `VaultApiException` (503/sealed): return last-known-good credentials and log Warning.
- Thread-safe: use `SemaphoreSlim(1,1)` for cache update.

### VaultRotatingConnectionStringProvider.cs
- Polls `IVaultCredentialProvider.GetDatabaseCredentialsAsync` every 45 seconds via `PeriodicTimer`.
- On password change detected: acquire PostgreSQL advisory lock (lockId = `serviceName.GetHashCode()`), call `NpgsqlDataSource.Clear()`, rebuild `NpgsqlDataSource` with new password, release lock.
- Implements `IConnectionStringProvider` (also defined in this project).
- Advisory lock timeout: 10 seconds. If timeout, skip this cycle (log Debug).
- Runs as `IHostedService`.

### AddVaultRotatingPostgres extension method
```csharp
public static IServiceCollection AddVaultRotatingPostgres(
    this IServiceCollection services,
    string roleName,
    IConfiguration configuration)
```
Registers `VaultCredentialProvider`, `VaultRotatingConnectionStringProvider`, and replaces the static `NpgsqlDataSource` registration with a factory that reads from `IConnectionStringProvider`.

---

## Acceptance

```bash
dotnet build src/BuildingBlocks/BuildingBlocks.Vault/
dotnet test tests/BuildingBlocks.Vault.Unit/  # unit tests you write as part of this brief
```

Unit tests required (in `tests/BuildingBlocks.Vault.Unit/`):
- `VaultCredentialProvider_returns_cached_credentials_within_expiry`
- `VaultCredentialProvider_fetches_new_credentials_after_expiry`
- `VaultCredentialProvider_returns_stale_on_vault_503`
- `VaultRotatingConnectionStringProvider_clears_pool_on_password_change`
- `VaultRotatingConnectionStringProvider_skips_pool_drain_when_password_unchanged`
- `VaultRotatingConnectionStringProvider_skips_cycle_when_advisory_lock_not_acquired` (mock lock timeout)

---

## Anti-stuck

- Use `VaultSharp`, not raw `HttpClient`. The `IVaultClient` interface from VaultSharp is mockable.
- `NpgsqlDataSource.Clear()` drains idle connections only — do NOT call `NpgsqlConnection.ClearAllPools()`.
- Advisory lock `lockId` must fit in an `int` (PostgreSQL pg_advisory_lock takes bigint, but restrict to int range for safety). Use `Math.Abs(serviceName.GetHashCode())`.
- Do NOT use `NpgsqlDataSource` constructor directly — use `NpgsqlDataSourceBuilder` so Npgsql's internal pool management remains intact.
- This brief does NOT wire the provider into any service — that is R6.

---

## Done-report format

```
brief: R2
status: done | blocked
files_changed:
  - src/BuildingBlocks/BuildingBlocks.Vault/BuildingBlocks.Vault.csproj
  - src/BuildingBlocks/BuildingBlocks.Vault/VaultCredentialProvider.cs
  - src/BuildingBlocks/BuildingBlocks.Vault/VaultRotatingConnectionStringProvider.cs
  - src/BuildingBlocks/BuildingBlocks.Vault/IVaultCredentialProvider.cs
  - src/BuildingBlocks/BuildingBlocks.Vault/IConnectionStringProvider.cs
  - tests/BuildingBlocks.Vault.Unit/VaultCredentialProviderTests.cs
  - tests/BuildingBlocks.Vault.Unit/VaultRotatingConnectionStringProviderTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
