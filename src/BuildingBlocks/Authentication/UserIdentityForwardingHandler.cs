using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Haworks.BuildingBlocks.Authentication;

// DelegatingHandler that pulls the user id from the current HttpContext
// (set by the JWT validator on the BFF) and adds it as X-User-Id on the
// outbound request. Anonymous calls forward without the header — backend
// 401s if the route requires auth.
public sealed class UserIdentityForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    public const string HeaderName = "X-User-Id";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var user = accessor.HttpContext?.User;
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? user?.FindFirstValue("sub");

        if (!string.IsNullOrEmpty(userId) && !request.Headers.Contains(HeaderName))
        {
            request.Headers.Add(HeaderName, userId);
        }

        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
