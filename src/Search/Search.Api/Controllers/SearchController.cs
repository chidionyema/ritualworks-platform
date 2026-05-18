using System.ComponentModel.DataAnnotations;
using Haworks.Search.Application.Indexing;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Haworks.Search.Api.Controllers;

[ApiController]
[Route("search")]
[Authorize]
public sealed class SearchController : ControllerBase
{
    private readonly ISearchIndex _index;
    private readonly ILogger<SearchController> _logger;

    public SearchController(ISearchIndex index, ILogger<SearchController> logger)
    {
        _index = index;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromQuery, Required, MinLength(1), MaxLength(200)] string q,
        [FromQuery] Guid? categoryId,
        [FromQuery, Range(1, 10_000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var sanitized = SearchQuerySanitizer.Sanitize(q);
        if (sanitized.Length == 0)
        {
            return BadRequest(new { error = "q must contain at least one searchable term" });
        }

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        // Audit trail: structured log for every search query
        _logger.LogInformation(
            "SearchQuery executed. UserId={UserId}, Query={Query}, CategoryId={CategoryId}, Page={Page}, PageSize={PageSize}, Timestamp={Timestamp}",
            userId, sanitized, categoryId, page, pageSize, DateTime.UtcNow);

        var page_ = await _index.SearchAsync(new SearchQuery
        {
            Query = sanitized,
            CategoryFilter = categoryId,
            Page = page,
            PageSize = pageSize,
        }, ct);

        var hits = page_.Hits.Select(h => new SearchHitResponse
        {
            ProductId    = h.ProductId,
            Name         = h.Name,
            // Placeholder snippet — Meilisearch's attributesToHighlight will
            // populate this in v2. Spec §3.1 keeps the field stable.
            Snippet      = h.Name,
            CategoryId   = h.CategoryId,
            CategoryName = h.CategoryName,
            UnitPrice    = h.UnitPrice,
            IsInStock    = h.IsInStock,
            // Placeholder score — Meilisearch's showRankingScore will
            // populate this in v2. Same spec stability rationale.
            Score        = 1.0,
        }).ToArray();

        return Ok(new SearchResponse
        {
            Query     = q,
            CategoryId = categoryId,
            Page      = page,
            PageSize  = pageSize,
            TotalHits = page_.TotalHits,
            TookMs    = page_.TookMs,
            Hits      = hits,
        });
    }

    [HttpPost("saved")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveSearch(
        [FromBody] SearchQuery query,
        CancellationToken ct = default)
    {
        // IDOR prevention: userId MUST come from authenticated claims, never from request body
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { error = "Authenticated user identity required to save a search." });

        var id = Guid.NewGuid().ToString("N");
        await _index.RegisterSavedSearchAsync(id, userId, query, ct);

        _logger.LogInformation(
            "SavedSearch created. UserId={UserId}, SearchId={SearchId}, Query={Query}, Timestamp={Timestamp}",
            userId, id, query.Query, DateTime.UtcNow);

        return Created($"/search/saved/{id}", new { id });
    }
}
