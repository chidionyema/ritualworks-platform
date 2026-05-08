using Haworks.BuildingBlocks.Authentication;
using Microsoft.AspNetCore.Http;

namespace Haworks.BuildingBlocks.Extensions;

public static class HttpContextExtensions
{
    public static string? GetForwardedUserId(this HttpContext ctx)
        => ctx.Request.Headers.TryGetValue(UserIdentityForwardingHandler.HeaderName, out var v)
            ? v.ToString()
            : null;
}
