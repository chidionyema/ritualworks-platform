using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Catalog.Api.Controllers;

/// <summary>
/// Demo-only endpoints used by the portfolio site's interactive demos via
/// BffWeb. NOT part of catalog-svc's domain — these are deliberately
/// minimal HTTP surfaces that BffWeb's typed clients can hit to drive
/// patterns like circuit breakers against a real downstream service.
///
/// Access: <see cref="AllowAnonymousAttribute"/> because the demo surface
/// is public; per-session rate limiting handled at the BffWeb edge.
/// </summary>
[ApiController]
[Route("demo")]
[AllowAnonymous]
public sealed class DemoTestController : ControllerBase
{
    /// <summary>
    /// Always returns 503 ServiceUnavailable. Used by T2.3's circuit-breaker
    /// demo: BffWeb hits this endpoint via a typed HttpClient with a Polly
    /// circuit breaker; 2 consecutive 503s open the circuit. Subsequent
    /// "shouldFail=false" calls hit /health and reset the circuit.
    /// </summary>
    [HttpGet("fail")]
    public IActionResult AlwaysFail() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = "demo_failure",
            message = "Synthetic failure for circuit-breaker demo",
            timestamp = DateTime.UtcNow,
        });
}
