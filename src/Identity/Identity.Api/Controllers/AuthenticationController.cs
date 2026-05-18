using Microsoft.AspNetCore.Http;
using Haworks.Identity.Api.Models;
using Haworks.Identity.Application;
using Haworks.Identity.Application;
using Haworks.BuildingBlocks.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Haworks.Identity.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthenticationController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly Microsoft.AspNetCore.Antiforgery.IAntiforgery _antiforgery;

    public AuthenticationController(
        IMediator mediator,
        Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery)
    {
        _mediator = mediator;
        _antiforgery = antiforgery;
    }

    // API-only: Bearer tokens are not auto-sent by browsers, so CSRF protection is not required. See OWASP CSRF guidance for token-based auth.
    [HttpGet("csrf-token")]
    [IgnoreAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult GetAntiforgeryToken()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { token = tokens.RequestToken, headerName = tokens.HeaderName });
    }

    [HttpPost("register")]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new RegisterCommand(
            request.Username,
            request.Email,
            request.Password,
            HttpContext), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new AuthResponse(
            dto.Token,
            dto.RefreshToken,
            dto.UserId,
            dto.Username,
            dto.Email,
            dto.Expires,
            dto.Message);

        return CreatedAtAction(nameof(VerifyToken), response);
    }

    [HttpPost("login")]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new LoginCommand(
            request.Username,
            request.Password,
            HttpContext), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new AuthResponse(
            dto.Token,
            dto.RefreshToken,
            dto.UserId,
            dto.Username,
            dto.Email,
            dto.Expires,
            dto.Message);

        return Ok(response);
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new LogoutCommand(User, HttpContext), cancellationToken);

        if (result.IsSuccess)
            return Ok(new { message = "Logged out successfully" });

        return result.ToActionResult();
    }

    [Authorize]
    [HttpGet("verify-token")]
    [ProducesResponseType(typeof(TokenVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> VerifyToken(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new VerifyTokenQuery(User), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new TokenVerificationResponse(
            dto.UserId,
            dto.UserName,
            dto.IsAuthenticated,
            dto.Message);

        return Ok(response);
    }

    [HttpPost("refresh-token")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RefreshToken(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new RefreshTokenCommand(
            request.AccessToken,
            request.RefreshToken,
            HttpContext), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new AuthResponse(
            dto.Token,
            dto.RefreshToken,
            dto.UserId,
            dto.Username,
            dto.Email,
            dto.Expires,
            dto.Message);

        return Ok(response);
    }

#if DEBUG
    /// <summary>
    /// Debug endpoint for inspecting authentication state. Only available in DEBUG builds.
    /// </summary>
    [HttpGet("debug-auth")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult DebugAuth()
    {
        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return Unauthorized(new
            {
                message = "User not authenticated (or identity information missing).",
                claims = User.Claims.Select(c => new { c.Type, c.Value })
            });
        }

        var claims = User.Claims.ToDictionary(c => c.Type, c => c.Value);

        return Ok(new
        {
            message = "Authentication successful",
            authType = User.Identity!.AuthenticationType,
            userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            userName = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value,
            role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value,
            claims
        });
    }
#endif

    [HttpPost("service-token")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ServiceToken(
        [FromHeader(Name = "X-Service-Secret")] string? serviceSecret,
        CancellationToken ct)
    {
        var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedSecret = config["ServiceAuth:SharedSecret"];
        if (string.IsNullOrEmpty(expectedSecret) || !string.Equals(serviceSecret, expectedSecret, StringComparison.Ordinal))
            return Unauthorized(new { error = "Invalid service secret" });

        var result = await _mediator.Send(
            new Identity.Application.Commands.Auth.CreateServiceTokenCommand(), ct);
        return result.IsSuccess
            ? Ok(new { token = result.Value, expiresInMinutes = 30 })
            : StatusCode(500, new { error = "Failed to create service token" });
    }
}
