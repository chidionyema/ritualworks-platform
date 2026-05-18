using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

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
public sealed class DemoTestController(
    HybridCache cache,
    ILogger<DemoTestController> logger) : ControllerBase
{
    /// <summary>
    /// Always returns 503 ServiceUnavailable. Used by T2.3's circuit-breaker
    /// demo: BffWeb hits this endpoint via a typed HttpClient with a Polly
    /// circuit breaker; 2 consecutive 503s open the circuit. Subsequent
    /// "shouldFail=false" calls hit /health and reset the circuit.
    /// </summary>
    [HttpGet("fail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult AlwaysFail() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = "demo_failure",
            message = "Synthetic failure for circuit-breaker demo",
            timestamp = DateTime.UtcNow,
        });

    // T2.7: in-process chaos flag. When set, /demo/health-with-chaos
    // returns 503; otherwise it returns 200. BffWeb's chaos/trigger flips
    // this flag in catalog for N seconds — paired with the circuit
    // breaker demo, the breaker opens from real upstream chaos rather
    // than just the always-failing /demo/fail.
    //
    // Production design (per Phase 2 brief T2.7): chaos flag should live
    // in Vault KV (secret/data/chaos) so multi-instance services agree
    // on state and the flag survives restarts. The static here is the
    // dev/demo equivalent — single-process catalog-svc, single source of
    // truth.
    private static long s_chaosUntilTicks;

    public static bool IsChaosActive() =>
        DateTime.UtcNow.Ticks < Interlocked.Read(ref s_chaosUntilTicks);

    [HttpPost("chaos/trigger")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult TriggerChaos([FromBody] ChaosRequest request)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(request.DurationSeconds, 1, 300));
        Interlocked.Exchange(ref s_chaosUntilTicks, until.Ticks);
        logger.LogWarning(
            "CHAOS injected: scenario={Scenario}, duration={Duration}s, until={Until:O}",
            request.Scenario, request.DurationSeconds, until);
        return Ok(new
        {
            scenario = request.Scenario,
            durationSeconds = request.DurationSeconds,
            activeUntil = until,
            traceId = Guid.NewGuid().ToString("N"),
        });
    }

    [HttpPost("chaos/clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ClearChaos()
    {
        Interlocked.Exchange(ref s_chaosUntilTicks, 0);
        logger.LogInformation("CHAOS cleared manually");
        return Ok(new { cleared = true });
    }

    [HttpGet("health-with-chaos")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult HealthWithChaos() =>
        IsChaosActive()
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { chaos = true, message = "Chaos injection active" })
            : Ok(new { chaos = false, healthy = true });

    public sealed record ChaosRequest
    {
        public required string Scenario { get; init; }
        public required int DurationSeconds { get; init; }
    }

    /// <summary>
    /// T2.6: real cache-stampede demo. Fires N concurrent reads through
    /// HybridCache.GetOrCreateAsync against a fresh key (clears first to
    /// guarantee a miss). HybridCache's built-in singleflight collapses
    /// the concurrent factory invocations into one — only one DB-simulated
    /// hit happens regardless of N. The dbQueries counter proves it.
    ///
    /// Without singleflight (protectionMode='none'), this endpoint runs
    /// the factory directly per request — N hits.
    /// </summary>
    [HttpPost("cache-stampede")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Stampede([FromBody] StampedeRequest request, CancellationToken ct)
    {
        var key = $"demo:stampede:{request.CacheKey}:{Guid.NewGuid():N}";
        var dbQueries = 0;

        async ValueTask<string> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref dbQueries);
            await Task.Delay(request.SimulatedDbLatencyMs, token);
            return $"value-{Guid.NewGuid():N}";
        }

        if (string.Equals(request.ProtectionMode, "singleflight", StringComparison.Ordinal))
        {
            // HybridCache collapses concurrent factories for the same key.
            await Parallel.ForEachAsync(
                Enumerable.Range(0, request.ConcurrentRequests),
                ct,
                async (_, token) => await cache.GetOrCreateAsync(key, Factory, cancellationToken: token));
        }
        else
        {
            // Bypass cache — every request hits the factory directly.
            await Parallel.ForEachAsync(
                Enumerable.Range(0, request.ConcurrentRequests),
                ct,
                async (_, token) => await Factory(token));
        }

        logger.LogInformation(
            "Cache stampede demo: mode={Mode} concurrency={N} dbQueries={Q}",
            request.ProtectionMode, request.ConcurrentRequests, dbQueries);

        return Ok(new
        {
            sessionId = Guid.NewGuid(),
            protectionMode = request.ProtectionMode,
            cacheHits = request.ConcurrentRequests - dbQueries,
            cacheMisses = dbQueries,
            dbQueries,
        });
    }

    public sealed record StampedeRequest
    {
        public required int ConcurrentRequests { get; init; }
        public required string CacheKey { get; init; }
        public required string ProtectionMode { get; init; }
        public required int SimulatedDbLatencyMs { get; init; }
    }

    /// <summary>
    /// Idempotent demo-product seed for the cache-invalidation demo. The
    /// demo needs a real Product row (and a real Category to put it in) to
    /// exercise the cached GET / production PUT / production DELETE flow
    /// against real Postgres. This endpoint finds-or-creates both:
    ///  - "Demo Category" (if missing)
    ///  - "Demo Widget" product in that category (if missing)
    /// Returns the product's stable Guid so BffWeb can use it for subsequent
    /// proxy calls. Safe to call repeatedly.
    /// </summary>
    [HttpPost("cache/seed-demo-product")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SeedDemoProduct(
        [FromServices] IMediator mediator,
        [FromServices] ICategoryRepository categories,
        [FromServices] IProductRepository products,
        CancellationToken ct)
    {
        const string CategoryName = "Demo Category";
        const string ProductName = "Demo Widget";

        var existingCategories = await categories.ListAsync(ct);
        var demoCategory = existingCategories.FirstOrDefault(c => string.Equals(c.Name, CategoryName, StringComparison.Ordinal));
        if (demoCategory is null)
        {
            var createCategoryResult = await mediator.Send(
                new CreateCategoryCommand(CategoryName, "Auto-seeded by /demo/cache/seed-demo-product"),
                ct);
            if (createCategoryResult.IsFailure)
            {
                return StatusCode(500, new { error = createCategoryResult.Error.Message });
            }
            demoCategory = await categories.GetByIdAsync(createCategoryResult.Value, ct);
        }

        var existingProducts = await products.ListAsync(skip: 0, take: 100, categoryId: demoCategory!.Id, ct);
        var demoProduct = existingProducts.FirstOrDefault(p => string.Equals(p.Name, ProductName, StringComparison.Ordinal));
        if (demoProduct is null)
        {
            var createProductResult = await mediator.Send(
                new CreateProductCommand(
                    ProductName,
                    "Demo product for the portfolio cache-invalidation demo",
                    UnitPrice: 39.99m,
                    CategoryId: demoCategory.Id,
                    InitialStock: 1000),
                ct);
            if (createProductResult.IsFailure)
            {
                return StatusCode(500, new { error = createProductResult.Error.Message });
            }
            return Ok(new { productId = createProductResult.Value, created = true });
        }

        return Ok(new { productId = demoProduct.Id, created = false });
    }
}
