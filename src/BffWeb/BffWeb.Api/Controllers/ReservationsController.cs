using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// BFF passthrough to <c>catalog-svc</c> for the ADR-004 sync reservation
/// flow. Mirrors <see cref="SubscriptionsController"/>'s style: forwards
/// the request body, content-type, <c>Authorization</c> header, and
/// <c>X-Idempotency-Key</c> verbatim.
///
/// The <c>UserIdentityForwardingHandler</c> (A3) is registered on
/// the named <c>catalog-svc</c> client and stamps <c>X-User-Id</c>
/// automatically — no explicit wiring needed here.
/// </summary>
[ApiController]
[Route("api/checkout/reservations")]
public sealed class ReservationsController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;

    public ReservationsController(IHttpClientFactory httpFactory, ILogger<ReservationsController> logger)
    {
        _httpFactory = httpFactory;
    }

    /// <summary>Anonymous-allowed: ADR-004 supports guest pre-order holds.</summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromHeader(Name = "Authorization")] string? authorization,
        [FromHeader(Name = "X-Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct = default)
    {
        Request.EnableBuffering();
        using var streamContent = new StreamContent(Request.Body);
        return await ForwardAsync(HttpMethod.Post, "/api/checkout/reservations", streamContent, authorization, idempotencyKey, ct);
    }

    /// <summary>
    /// Confirm a pending reservation. The downstream catalog-svc endpoint
    /// is <c>[Authorize]</c>'d and reads the email claim from the JWT, so
    /// the BFF must require auth too — otherwise we'd forward an
    /// unauthenticated request and get a 401 back from catalog-svc.
    /// </summary>
    [HttpPost("{reservationId:guid}/confirm")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Confirm(
        Guid reservationId,
        [FromHeader(Name = "Authorization")] string? authorization,
        [FromHeader(Name = "X-Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct = default)
    {
        Request.EnableBuffering();
        using var streamContent = new StreamContent(Request.Body);
        return await ForwardAsync(
            HttpMethod.Post,
            $"/api/checkout/reservations/{reservationId}/confirm",
            streamContent,
            authorization,
            idempotencyKey,
            ct);
    }

    private async Task<IActionResult> ForwardAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        string? authorization,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient(BackendClients.Catalog);

        using var request = new HttpRequestMessage(method, path);

        if (content != null)
        {
            request.Content = content;
            if (Request.ContentType != null)
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Request.ContentType);
            }
        }

        // Preserve Authorization (the JWT bearer) for the downstream
        // [Authorize] confirm endpoint and for defence-in-depth checks at
        // catalog-svc.
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        // Forward the X-Idempotency-Key so the catalog-svc IdempotencyMiddleware
        // can dedupe replays at the backend (the BFF is stateless on this).
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);
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
