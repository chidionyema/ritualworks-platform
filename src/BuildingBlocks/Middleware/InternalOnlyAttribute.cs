using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Net;

namespace Haworks.BuildingBlocks.Middleware;

/// <summary>
/// Restricts an endpoint to internal callers only (localhost, private IP,
/// or Fly.io 6PN network). Returns 404 for external requests so the
/// endpoint is invisible, not just forbidden.
///
/// Combine with <c>[Authorize(Roles = "Admin,Service")]</c> for defense-in-depth:
/// network guard + auth guard.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InternalOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !IsInternal(remote))
        {
            context.Result = new NotFoundResult();
            return;
        }

        base.OnActionExecuting(context);
    }

    private static bool IsInternal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            // IPv4 private ranges: 10.x, 172.16-31.x, 192.168.x
            System.Net.Sockets.AddressFamily.InterNetwork =>
                bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168),

            // IPv6: Fly 6PN uses fdaa::/16 prefix, also allow ::1 and fe80::/10
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                address.IsIPv6LinkLocal
                || bytes[0] == 0xfd  // ULA (includes Fly 6PN fdaa::)
                || bytes[0] == 0xfc, // ULA range

            _ => false
        };
    }
}
