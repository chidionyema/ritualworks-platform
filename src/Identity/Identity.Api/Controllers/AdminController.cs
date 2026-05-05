using Haworks.BuildingBlocks.Vault;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Identity.Api.Controllers;

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
[AllowAnonymous]
public sealed class AdminController(
    IServiceProvider services,
    ILogger<AdminController> logger) : ControllerBase
{
    /// <summary>
    /// Forces a Vault credential refresh for a named AppRole. T2.4's
    /// vault-rotation demo posts here. If <see cref="IVaultService"/> isn't
    /// registered (Vault integration not wired into this identity-svc
    /// instance), logs a warning and returns 202 anyway — keeps the demo
    /// HTTP contract honest while not pretending to rotate something that
    /// doesn't exist.
    /// </summary>
    [HttpPost("vault/rotate-credentials")]
    public IActionResult RotateCredentials([FromQuery] string roleName = "identity-jwt")
    {
        var vault = services.GetService<IVaultService>();
        if (vault is null)
        {
            logger.LogWarning(
                "Vault rotate requested for role={RoleName} but IVaultService is not registered. " +
                "Returning 202 to keep demo contract honest. Wire AddVaultIntegration() in identity-svc " +
                "DI to make this rotate the real Vault lease.",
                roleName);
            return Accepted(new
            {
                roleName,
                status = "AcceptedNoVault",
                message = "Demo endpoint reached; vault integration not registered.",
            });
        }

        // Fire-and-forget: rotation can take several hundred ms; don't block
        // the HTTP request. Frontend gets the SignalR per-stage progression
        // from BffWeb's simulated stages (real per-stage events would need
        // IVaultService to publish them — bigger scope, tracked separately).
        _ = Task.Run(async () =>
        {
            try
            {
                await vault.RefreshCredentials(roleName);
                logger.LogInformation("Vault credentials refreshed for role={RoleName}", roleName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Vault rotation failed for role={RoleName}", roleName);
            }
        });

        return Accepted(new { roleName, status = "Rotating" });
    }
}
