using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// BFF passthrough to <c>payments-svc</c> for subscription operations.
/// </summary>
[ApiController]
[Route("api/subscriptions")]
public sealed class SubscriptionsController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SubscriptionsController> _logger;

    public SubscriptionsController(IHttpClientFactory httpFactory, ILogger<SubscriptionsController> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        return await ForwardAsync(HttpMethod.Get, "/api/subscriptions/status", null, ct);
    }

    [HttpPost("create-checkout-session")]
    public async Task<IActionResult> CreateCheckoutSession(CancellationToken ct)
    {
        // For POST, we read the body and forward it.
        Request.EnableBuffering();
        using var streamContent = new StreamContent(Request.Body);
        return await ForwardAsync(HttpMethod.Post, "/api/subscriptions/create-checkout-session", streamContent, ct);
    }

    private async Task<IActionResult> ForwardAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
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
        if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authHeader.ToArray());
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
