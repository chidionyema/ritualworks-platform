# A1 — BuildingBlocks: AddPlatformAuthentication + UserIdentityForwardingHandler + GetForwardedUserId

## Goal

Add three small reusable pieces to `BuildingBlocks` so every backend service can wire JWT validation with one line, the BFF can forward user identity downstream as `X-User-Id`, and any controller can read the forwarded id without knowing JWT internals.

## Phase / blocks-on

Phase A. **Sequential blocker for A2/A3/A4.** No prior briefs.

## Inputs (read in order, all of them, before writing)

1. `docs/agent-briefs/platform/README.md`.
2. `docs/agent-briefs/platform-completion-spec.md` — Phase A "Design" + "Scope of work" sections.
3. `src/BuildingBlocks/Extensions/ServiceDefaults.cs` — file you'll add to (or sibling-of). Look at the existing extension shape (`IHostApplicationBuilder` chain).
4. `src/Identity/Identity.Api/Program.cs` — Identity already wires `AddAuthentication().AddJwtBearer(...)` for issuing tokens. Read it to learn (a) what config keys hold the signing key (`Jwt:SigningKeyPem` / `Jwt:Issuer` / `Jwt:Audience`) and (b) whether it uses RSA or HMAC. The new `AddPlatformAuthentication` mirrors the **validation** half of that setup.
5. `src/BuildingBlocks.Testing/Authentication/TestAuthenticationHandler.cs` — the existing test handler. Confirm the constants the test handler exposes (scheme name, default user id) so the new helpers play nicely with it under Test.
6. `src/Payments/Payments.Application/DTOs/Subscriptions/SubscriptionStatusDto.cs` — exists from the merged C1, so the codebase has an example of the patterns we're protecting.

## Deliverable

### Three files in BuildingBlocks

`src/BuildingBlocks/Extensions/AuthenticationExtensions.cs`:

```csharp
public static IServiceCollection AddPlatformAuthentication(
    this IServiceCollection services,
    IConfiguration configuration)
{
    var issuer    = configuration["Jwt:Issuer"]    ?? throw new InvalidOperationException("Jwt:Issuer missing");
    var audience  = configuration["Jwt:Audience"]  ?? throw new InvalidOperationException("Jwt:Audience missing");
    var keyPem    = configuration["Jwt:SigningKeyPem"] ?? throw new InvalidOperationException("Jwt:SigningKeyPem missing");

    var rsa = RSA.Create();
    rsa.ImportFromPem(DecodeMaybeBase64(keyPem));

    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer            = issuer,
                ValidAudience          = audience,
                IssuerSigningKey       = new RsaSecurityKey(rsa),
                ValidateIssuer         = true,
                ValidateAudience       = true,
                ValidateLifetime       = true,
                ValidateIssuerSigningKey = true,
                ClockSkew              = TimeSpan.FromMinutes(1),
            };
        });

    services.AddAuthorizationBuilder()
        .AddDefaultPolicy("Default", p => p.RequireAuthenticatedUser());

    return services;
}
```

`DecodeMaybeBase64` mirrors the helper Identity already uses — read the Identity Program.cs to see whether the PEM is base64-encoded or plain. Same shape on both sides.

`src/BuildingBlocks/Authentication/UserIdentityForwardingHandler.cs`:

```csharp
// DelegatingHandler that pulls the user id from the current HttpContext
// (set by the JWT validator on the BFF) and adds it as X-User-Id on the
// outbound request. Anonymous calls forward without the header — backend
// 401s if the route requires auth.
public sealed class UserIdentityForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    public const string HeaderName = "X-User-Id";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var user = accessor.HttpContext?.User;
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? user?.FindFirstValue("sub");

        if (!string.IsNullOrEmpty(userId) && !request.Headers.Contains(HeaderName))
        {
            request.Headers.Add(HeaderName, userId);
        }

        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
```

`src/BuildingBlocks/Extensions/HttpContextExtensions.cs`:

```csharp
public static class HttpContextExtensions
{
    public static string? GetForwardedUserId(this HttpContext ctx)
        => ctx.Request.Headers.TryGetValue(UserIdentityForwardingHandler.HeaderName, out var v)
            ? v.ToString()
            : null;
}
```

### NuGet packages

Add to `src/BuildingBlocks/Haworks.BuildingBlocks.csproj`:

- `Microsoft.AspNetCore.Authentication.JwtBearer` (latest matching net9.0)
- `System.IdentityModel.Tokens.Jwt` (transitive but make explicit)

(Identity already references these — match its versions.)

### Tests

`tests/BuildingBlocks.Tests/Authentication/UserIdentityForwardingHandlerTests.cs` (create the project if it doesn't exist; otherwise add to existing):

- `Forwards_X_User_Id_when_HttpContext_has_authenticated_user`
- `Does_not_forward_when_HttpContext_is_null` (background process scenario)
- `Does_not_overwrite_existing_X_User_Id_header`
- `Reads_from_NameIdentifier_then_falls_back_to_sub`

Use `DefaultHttpContextAccessor` + a stubbed `ClaimsPrincipal` for the tests.

If `tests/BuildingBlocks.Tests/` doesn't exist as a project, create it (csproj mirroring `tests/Search.Unit/Search.Unit.csproj` style — xUnit + FluentAssertions + Moq) and reference it from `HaworksPlatform.sln`.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/BuildingBlocks.Tests -c Release   # if you created it; else equivalent
```

All green. The new helpers compile; the handler tests pass.

## Hard stops

- Do **NOT** modify any service's `Program.cs` (A2/A3 do that).
- Do **NOT** modify any controller (A4 does that).
- Do **NOT** add `AddHttpContextAccessor` to any service's DI — the consuming briefs do it.
- Do **NOT** invent config keys. Use `Jwt:Issuer / Jwt:Audience / Jwt:SigningKeyPem` exactly as Identity uses them.
- Do **NOT** swap RSA for HMAC unless Identity itself uses HMAC — match what's there.
- Do **NOT** push, deploy, force, amend, rebase, or open PRs.

## Done-report

Standard format. Confirm:
- The 3 helper files are in place under `src/BuildingBlocks/`.
- Identity's existing `AddJwtBearer` config matches the new validator's params (issuer/audience/key all the same shape).
- New handler tests pass.
