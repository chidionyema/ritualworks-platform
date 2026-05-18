using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// BFF proxy to <c>location-svc</c>.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/locations")]
[Authorize]
public sealed class LocationsController(IHttpClientFactory httpFactory) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] object command, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient(BackendClients.Location);
        var response = await http.PostAsJsonAsync("/api/addresses", command, ct);
        
        var body = await response.Content.ReadAsStringAsync(ct);
        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
            Content = body
        };
    }
}
