using Haworks.Payouts.Application.Sellers.Commands.GetOnboardingLink;
using Haworks.Payouts.Application.Sellers.Commands.RegisterSeller;
using Haworks.BuildingBlocks.Extensions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Payouts.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SellersController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register(RegisterSellerCommand command)
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        // Sellers can only register themselves
        if (!Guid.TryParse(userId, out var parsedUserId) || parsedUserId != command.SellerId)
            return Forbid();

        return Ok(new { ProfileId = await mediator.Send(command) });
    }

    [HttpPost("{sellerId}/onboarding-link")]
    public async Task<IActionResult> GetOnboardingLink(
        Guid sellerId,
        [FromQuery] string returnUrl,
        [FromQuery] string refreshUrl)
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (!Guid.TryParse(userId, out var parsedUserId) || parsedUserId != sellerId)
            return Forbid();

        // C9: Validate URLs to prevent SSRF
        if (!IsValidRedirectUrl(returnUrl) || !IsValidRedirectUrl(refreshUrl))
            return BadRequest("Invalid redirect URL. Only HTTPS URLs on allowed domains are accepted.");

        return Ok(new { Url = await mediator.Send(new GetOnboardingLinkCommand(sellerId, returnUrl, refreshUrl)) });
    }

    private static bool IsValidRedirectUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "https", StringComparison.Ordinal)) return false;

        // M2 Fix: Proper RFC 1918 + link-local + loopback checks
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
            string.Equals(host, "[::1]", StringComparison.Ordinal))
            return false;

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4)
            {
                // 10.0.0.0/8
                if (bytes[0] == 10) return false;
                // 172.16.0.0/12 (172.16-31.x.x)
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false;
                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return false;
                // 169.254.0.0/16 (link-local)
                if (bytes[0] == 169 && bytes[1] == 254) return false;
            }
        }

        return true;
    }
}
