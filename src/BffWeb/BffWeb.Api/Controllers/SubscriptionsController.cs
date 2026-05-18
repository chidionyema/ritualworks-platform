using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// BFF passthrough to <c>payments-svc</c> for subscription operations.
/// </summary>
[Authorize]
[ApiController]
[Route("api/subscriptions")]
public sealed class SubscriptionsController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;

    public SubscriptionsController(IHttpClientFactory httpFactory, ILogger<SubscriptionsController> logger)
    {
        _httpFactory = httpFactory;
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> GetStatus(
        [FromHeader(Name = "Authorization")] string? authorization,
        CancellationToken ct = default)
    {
        return ForwardAsync(HttpMethod.Get, "/api/subscriptions/status", null, authorization, ct);
    }

    [HttpPost("create-checkout-session")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateCheckoutSession(
        [FromHeader(Name = "Authorization")] string? authorization,
        CancellationToken ct = default)
    {
        // For POST, we read the body and forward it.
        Request.EnableBuffering();
        using var streamContent = new StreamContent(Request.Body);
        return await ForwardAsync(HttpMethod.Post, "/api/subscriptions/create-checkout-session", streamContent, authorization, ct);
    }

    private async Task<IActionResult> ForwardAsync(HttpMethod method, string path, HttpContent? content, string? authorization, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient(BackendClients.Payments);

        using var request = new HttpRequestMessage(method, path);

        if (content != null)
        {
            request.Content = content;
            if (Request.ContentType != null)
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Request.ContentType);
            }
        }

        // Preserve Authorization header
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            ContentType = contentType,
            Content = body,
        };
    }
}
