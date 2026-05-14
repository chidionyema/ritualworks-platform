using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// BFF passthrough to <c>search-svc</c>. The actual ranking + Meilisearch
/// integration lives there; the BFF just forwards the query string and
/// the response body. This keeps the search-svc internal-only (no public
/// IP) while letting the public BFF host expose the route.
/// </summary>
[ApiController]
[Route("api/search")]
public sealed class SearchController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SearchController> _logger;

    public SearchController(IHttpClientFactory httpFactory, ILogger<SearchController> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Search(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(BackendClients.Search);
        var path = "/search" + Request.QueryString.Value;

        using var upstream = await http.GetAsync(path, ct);
        var body = await upstream.Content.ReadAsStringAsync(ct);

        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
        return new ContentResult
        {
            StatusCode  = (int)upstream.StatusCode,
            ContentType = contentType,
            Content     = body,
        };
    }

    [HttpPost("saved")]
    [Authorize]
    public async Task<IActionResult> SaveSearch([FromBody] object query, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(BackendClients.Search);
        var path = "/search/saved";

        using var upstream = await http.PostAsJsonAsync(path, query, ct);
        var body = await upstream.Content.ReadAsStringAsync(ct);

        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
        return new ContentResult
        {
            StatusCode  = (int)upstream.StatusCode,
            ContentType = contentType,
            Content     = body,
        };
    }
}
