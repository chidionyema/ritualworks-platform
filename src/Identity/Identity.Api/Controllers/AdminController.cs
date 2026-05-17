using Haworks.BuildingBlocks.Vault;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Identity.Api.Controllers;

/// <summary>
/// HttpClient marker for the dedicated Vault health probe. Registered
/// with a 2s timeout so a paused vault container surfaces as a 5xx
/// fast in the topology auto-prober.
/// </summary>
public sealed class VaultProbeClient
{
    public HttpClient Client { get; }
    public Uri Address { get; }
    public VaultProbeClient(HttpClient client, Uri address)
    {
        Client = client;
        Address = address;
    }
}

/// <summary>
/// Operational endpoints for identity-svc — exposed for the portfolio
/// site's demo flow via BffWeb. NOT part of identity's user-facing
/// surface; in production these MUST be locked behind a localhost-only
/// or mesh-only middleware (TODO: layer guard before prod deploy).
///
/// AllowAnonymous + minimal — same pattern as Catalog.Api/DemoTestController.
/// </summary>
[ApiController]
[Route("admin")]
[Authorize(Roles = "Admin,Service")]
public sealed class AdminController(
    // IServiceProvider so we can resolve Vault deps at action time instead
    // of constructor time. When Vault:Enabled=false (which the bootstrap
    // shim sets if vault was unreachable at boot — fail-open path),
    // VaultProbeClient and IVaultService aren't registered. Constructor-
    // time injection used to crash AdminController activation outright;
    // this path returns a clean "Disabled" response instead.
    IServiceProvider services,
    IConfiguration configuration,
    IPublishEndpoint publishEndpoint,
    ILogger<AdminController> logger) : ControllerBase
{
    private bool VaultEnabled => configuration.GetValue("Vault:Enabled", false);

    /// <summary>
    /// Forces a Vault credential refresh for a named AppRole and emits
    /// per-stage events through the EF outbox so BffWeb's
    /// <c>VaultRotationStageBridge</c> can stream the lifecycle into the
    /// portfolio's vault-rotation demo SignalR stream.
    ///
    /// <see cref="IVaultService"/> is registered via
    /// <c>services.AddVaultIntegration(...)</c> in
    /// <c>Identity.Infrastructure.DependencyInjection</c>.
    /// </summary>
    /// <summary>
    /// Real round-trip to Vault via <see cref="IVaultService.GetTokenInfoAsync"/>.
    /// Honest: returns 503 if Vault is unreachable instead of any cached
    /// fallback — pausing the vault container must surface as a real
    /// failure in the topology map's auto-prober, not a cosmetic green
    /// dot. <see cref="IVaultService.LeaseDurationFor"/> + <see cref="IVaultService.LeaseExpiryFor"/>
    /// supply the role-specific lease info from the cache populated by
    /// the most recent vault refresh.
    /// </summary>
    [HttpGet("vault/status")]
    public async Task<IActionResult> GetVaultStatus(CancellationToken ct)
    {
        if (!VaultEnabled)
        {
            // Vault disabled at runtime (config flag false, or bootstrap
            // shim hit fail-open because vault was unreachable at startup).
            // Returning 200 with status="Disabled" is honest: identity is
            // up and serving, this *demo* feature just isn't wired here.
            return Ok(new
            {
                status = "Disabled",
                message = "Vault is not enabled in this environment.",
                enabled = false,
            });
        }

        var probe = services.GetService<VaultProbeClient>();
        var vault = services.GetService<IVaultService>();
        if (probe is null || vault is null)
        {
            // Config says enabled but DI didn't register the deps — means
            // the conditional registration in Program.cs ran with stale
            // config. Treat as disabled rather than crash.
            return Ok(new
            {
                status = "Disabled",
                message = "Vault is enabled in config but probe client is unregistered.",
                enabled = false,
            });
        }

        // Raw HTTP probe to vault's /v1/sys/health endpoint (unauthenticated
        // by spec). This bypasses IVaultService entirely — its
        // GetTokenInfoAsync reads from the local lease cache and would
        // happily report "healthy" even with the vault container paused.
        // 2s timeout on the typed client; container chaos surfaces fast.
        try
        {
            using var resp = await probe.Client.GetAsync("/v1/sys/health", ct);
            // /v1/sys/health uses non-2xx codes for non-active states
            // (sealed/standby/etc) but the body still parses. Any reachable
            // response counts as "vault is alive" for our purposes.
            var body = await resp.Content.ReadAsStringAsync(ct);
            const string roleName = "haworks-identity";
            var leaseExpiry = vault.LeaseExpiryFor(roleName);
            var leaseTtlSeconds = (int)Math.Max(
                0, (leaseExpiry - DateTime.UtcNow).TotalSeconds);
            return Ok(new
            {
                status = "Healthy",
                vaultAddress = probe.Address.ToString(),
                vaultStatusCode = (int)resp.StatusCode,
                vaultBody = body,
                roleName,
                leaseTtlSeconds,
                leaseExpiry,
            });
        }
        catch (Exception ex)
        {
            // Container paused / network unreachable / TLS failure all
            // land here and surface as 503 to the BFF.
            logger.LogWarning(ex, "Vault HTTP probe failed");
            return StatusCode(503, new
            {
                status = "Unreachable",
                vaultAddress = probe.Address.ToString(),
                error = ex.GetType().Name,
                message = ex.Message,
            });
        }
    }

    [HttpPost("vault/rotate-credentials")]
    public IActionResult RotateCredentials(
        // Default to identity's real Vault dynamic-Postgres role (per
        // deploy/aspire/manifests/database/roles.json). Calling
        // RefreshCredentials issues a fresh ephemeral DB user under this
        // role — that's the meaningful "rotation" for the demo.
        [FromQuery] string roleName = "haworks-identity",
        [FromQuery] Guid? sessionId = null)
    {
        if (!VaultEnabled)
        {
            return StatusCode(503, new { status = "Disabled", message = "Vault is not enabled in this environment." });
        }
        var vault = services.GetService<IVaultService>();
        if (vault is null)
        {
            return StatusCode(503, new { status = "Disabled", message = "Vault service is not registered." });
        }
        var resolvedSession = sessionId ?? Guid.NewGuid();

        // Fire-and-forget: the actual Vault round-trip can take several
        // hundred ms, but the demo wants the HTTP response immediately so
        // the frontend can subscribe to the SignalR stream of stage events
        // without holding the request open.
        _ = Task.Run(async () =>
        {
            const int newVersion = 1;
            const string previousVersion = "current";
            try
            {
                await PublishStageAsync(resolvedSession, "started", newVersion, previousVersion);

                // The single stage that's bound to a real Vault round-trip
                // today. The IVaultService cycles the AppRole-backed
                // credential store under this role — RefreshCredentials
                // re-issues the dynamic Postgres lease tied to it.
                // Lazy-init: VaultService requires InitializeAsync to be
                // called once before its first use; idempotent so safe to
                // call here on every rotate.
                await vault.InitializeAsync();
                await vault.RefreshCredentials(roleName);
                await PublishStageAsync(resolvedSession, "credentials-fetched", newVersion, previousVersion);

                // 'applied' / 'validated' / 'revoked-old' aren't surfaced as
                // distinct hooks on IVaultService today — these publishes
                // are real broker round-trips but their semantic is
                // best-effort. Adding IProgress<VaultStage> to
                // IVaultService.RefreshCredentials would let each stage
                // correspond to a real Vault sub-operation; tracked
                // separately.
                await PublishStageAsync(resolvedSession, "applied", newVersion, previousVersion);
                await PublishStageAsync(resolvedSession, "validated", newVersion, previousVersion);
                await PublishStageAsync(resolvedSession, "revoked-old", newVersion, previousVersion);

                logger.LogInformation("Vault credentials refreshed for role={RoleName}", roleName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Vault rotation failed for role={RoleName}", roleName);
            }
        });

        return Accepted(new { roleName, status = "Rotating", sessionId = resolvedSession });
    }

    private Task PublishStageAsync(Guid sessionId, string stage, int newVersion, string previousVersion) =>
        publishEndpoint.Publish(new Haworks.Contracts.Identity.VaultRotationStageEvent
        {
            SessionId = sessionId,
            Stage = stage,
            NewVersion = newVersion,
            PreviousVersion = previousVersion,
            Timestamp = DateTime.UtcNow,
        });
}
