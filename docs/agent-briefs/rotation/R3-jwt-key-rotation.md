# R3 — JWT Key Rotation + IOptionsMonitor Wiring

**Brief:** R3 | **Spec:** `docs/agent-briefs/secret-rotation-spec.md`
**Phase:** 2 (parallel with R2 — both require R1 complete)
**Time budget:** 30 min

---

## Inputs

Read before touching anything:
- `src/Identity/Identity.Api/appsettings.json` — current JWT config section
- `infra/vault/secrets/kv-layout.json` — `identity/jwt` path and keys
- `src/Contracts/` — existing contract pattern (look at any event record for the style)

---

## Deliverable

### 1. New event contract (`src/Contracts/Secrets/JwtKeyRotatedEvent.cs`)
```csharp
public sealed record JwtKeyRotatedEvent
{
    public required Guid RotationId { get; init; }
    public required DateTimeOffset RotatedAt { get; init; }
    // No key material — consumers re-fetch from Vault
}
```

### 2. Identity service JWT options reload

In `Identity.Infrastructure/DependencyInjection.cs`, change the JWT options registration from:
```csharp
services.Configure<JwtOptions>(config.GetSection("Jwt"));
```
to use `IOptionsMonitor<JwtOptions>` so live reload works when Vault updates the config source.

Verify that `JwtTokenService` (or equivalent token issuer) takes `IOptionsMonitor<JwtOptions>` and calls `.CurrentValue` on every token issuance — not a snapshot at DI registration time.

### 3. JwtKeyRotatedConsumer in identity-svc

```csharp
public class JwtKeyRotatedConsumer : IConsumer<JwtKeyRotatedEvent>
{
    private readonly IOptionsMonitor<JwtOptions> _jwtOptions;
    private readonly IVaultCredentialProvider _vault;  // re-uses BuildingBlocks.Vault

    public async Task Consume(ConsumeContext<JwtKeyRotatedEvent> context)
    {
        // Re-fetch new key from Vault: secret/identity/jwt → Key
        // Update IOptionsMonitor via the underlying IOptionsMonitorCache<JwtOptions>
        // For the overlap period (15 min), keep PreviousKey for validation
        // Log the rotation event with RotationId (no key material in logs)
    }
}
```

### 4. Dual-key validation

The identity service's token validation must accept both current and previous signing keys during the overlap window. Implement `DualKeyJwtValidator` that:
- Tries to validate with the current key first.
- On `SecurityTokenSignatureKeyNotFoundException`, retries with the previous key.
- Previous key is stored in `JwtOptions.PreviousKey` (nullable) and cleared 15 minutes after rotation.

---

## Acceptance

```bash
dotnet build src/Identity/
dotnet test tests/Identity.Unit/  # new tests + existing must pass
```

Unit tests required (in `tests/Identity.Unit/`):
- `JwtTokenService_uses_current_options_value_not_snapshot`
- `DualKeyJwtValidator_validates_token_signed_with_previous_key`
- `DualKeyJwtValidator_rejects_token_signed_with_unknown_key`
- `DualKeyJwtValidator_clears_previous_key_after_overlap_window`
- `JwtKeyRotatedConsumer_reloads_key_from_vault_on_event`

---

## Anti-stuck

- Do NOT store key material in any event, log, or metric label.
- `IOptionsMonitorCache<JwtOptions>` can be cleared via `TryRemove(string name)` — this triggers a reload from the configuration source. Look up how ASP.NET Core's `OptionsMonitorCache` works before implementing.
- The overlap period is 15 minutes — match `CheckoutOptions.PaymentExpiryMinutes` which is currently also 15 minutes. Do not hard-code — read from `JwtOptions.OverlapMinutes` (add this field, default 15).
- Do not add a Vault configuration provider in this brief — the key reload is driven by the `JwtKeyRotatedEvent` consumer, not by a polling config provider.

---

## Done-report format

```
brief: R3
status: done | blocked
files_changed:
  - src/Contracts/Secrets/JwtKeyRotatedEvent.cs
  - src/Identity/Identity.Infrastructure/DependencyInjection.cs
  - src/Identity/Identity.Application/Services/DualKeyJwtValidator.cs
  - src/Identity/Identity.Application/Consumers/JwtKeyRotatedConsumer.cs
  - tests/Identity.Unit/DualKeyJwtValidatorTests.cs
  - tests/Identity.Unit/JwtKeyRotatedConsumerTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
