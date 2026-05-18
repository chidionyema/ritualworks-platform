using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Haworks.BuildingBlocks.Common;
using Haworks.Identity.Application;
using Haworks.Identity.Application.Options;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Haworks.Identity.Api.Controllers;

/// <summary>
/// External-login HTTP surface (Google, Microsoft, Facebook).
///
/// Flow:
///   1. SPA hits /challenge/{provider} → 302 redirect to provider's auth page
///   2. User authenticates at provider
///   3. Provider redirects to /api/external-authentication/{provider}-callback
///      (configured in Program.cs's AddGoogle/AddMicrosoftAccount/AddFacebook)
///   4. ASP.NET handles the OAuth code exchange + populates ExternalLoginInfo
///   5. ASP.NET redirects to /callback which dispatches ExternalLoginCallbackCommand
///   6. Command finds-or-creates the user, issues a JWT, returns to caller
///
/// Per ADR-0009: external-provider account IDs map to OUR opaque UserId via
/// AspNetUserLogins join table. Other services only see the canonical UserId.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/external-authentication")]
public sealed class ExternalAuthenticationController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly SignInManager<User> _signInManager;
    private readonly SecurityOptions _securityOptions;
    private readonly ILogger<ExternalAuthenticationController> _logger;

    public ExternalAuthenticationController(
        IMediator mediator,
        SignInManager<User> signInManager,
        IOptions<SecurityOptions> securityOptions,
        ILogger<ExternalAuthenticationController> logger)
    {
        _mediator = mediator;
        _signInManager = signInManager;
        _securityOptions = securityOptions.Value;
        _logger = logger;
    }

    [HttpGet("challenge/{provider}")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Challenge(string provider, [FromQuery] string? redirectUrl, CancellationToken ct)
    {
        var providers = await _signInManager.GetExternalAuthenticationSchemesAsync();
        if (!providers.Any(p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Invalid authentication provider requested: {Provider}", provider);
            return BadRequest($"Provider '{provider}' is not supported");
        }

        if (!IsValidRedirectUrl(redirectUrl))
        {
            _logger.LogWarning("Invalid redirect URL rejected: {RedirectUrl}", redirectUrl);
            redirectUrl = Url.Action(nameof(Callback), "ExternalAuthentication");
        }

        var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    [HttpGet("callback")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Callback(CancellationToken ct)
    {
        var result = await _mediator.Send(new ExternalLoginCallbackCommand(HttpContext), ct);
        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        return Ok(new
        {
            token = dto.Token,
            refreshToken = dto.RefreshToken,
            expires = dto.Expires,
            message = dto.Message,
            user = new
            {
                id = dto.UserId,
                userName = dto.Username,
                email = dto.Email,
            },
        });
    }

    [HttpGet("providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAvailableProviders(CancellationToken ct)
    {
        var result = await _mediator.Send(new GetAvailableProvidersQuery(), ct);
        return result.IsSuccess
            ? Ok(new { providers = result.Value })
            : result.ToActionResult();
    }

    [Authorize]
    [HttpPost("link/{provider}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LinkExternalLogin(string provider, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var info = await _signInManager.GetExternalLoginInfoAsync();

        var result = await _mediator.Send(new LinkExternalLoginCommand(userId!, provider, info), ct);

        if (result.IsSuccess && result.Value.RequiresChallenge)
        {
            var redirectUrl = Url.Action(nameof(LinkCallback), "ExternalAuthentication");
            var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl, userId);
            return Challenge(properties, provider);
        }

        return result.IsSuccess
            ? Ok(new { Message = result.Value.Message })
            : result.ToActionResult();
    }

    [HttpGet("link-callback")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LinkCallback(CancellationToken ct)
    {
        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (info is null)
            return BadRequest("Error getting external login information");

        var userId = info.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var result = await _mediator.Send(new LinkExternalLoginCommand(userId!, info.LoginProvider, info), ct);

        return result.IsSuccess
            ? Ok(new { Message = $"Successfully linked {info.LoginProvider} login to your account" })
            : result.ToActionResult();
    }

    [Authorize]
    [HttpDelete("unlink/{provider}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RemoveExternalLogin(string provider, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var result = await _mediator.Send(new UnlinkExternalLoginCommand(userId!, provider), ct);

        return result.IsSuccess
            ? Ok(new { Message = $"Successfully removed {provider} login from your account" })
            : result.ToActionResult();
    }

    [Authorize]
    [HttpGet("logins")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetUserLogins(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var result = await _mediator.Send(new GetUserLoginsQuery(userId!), ct);

        return result.IsSuccess
            ? Ok(new { Logins = result.Value })
            : result.ToActionResult();
    }

    private bool IsValidRedirectUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
            return false;
        if (!uri.IsAbsoluteUri)
        {
            // Must start with / and not be schema-relative (//) and not contain ..
            return url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains("..", StringComparison.Ordinal);
        }

        var currentHost = HttpContext.Request.Host.Host;
        return _securityOptions.AllowedRedirectHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || string.Equals(uri.Host, currentHost, StringComparison.OrdinalIgnoreCase);
    }
}
