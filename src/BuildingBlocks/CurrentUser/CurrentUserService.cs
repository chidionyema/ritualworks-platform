using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Haworks.BuildingBlocks.CurrentUser;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _contextAccessor;

    public CurrentUserService(IHttpContextAccessor contextAccessor)
    {
        _contextAccessor = contextAccessor;
    }

    public string? UserId =>
        _contextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? _contextAccessor.HttpContext?.User?.FindFirst("sub")?.Value
        ?? _contextAccessor.HttpContext?.Request.Headers["X-User-Id"].FirstOrDefault();

    public string? ClientIp =>
        _contextAccessor.HttpContext?.Connection.RemoteIpAddress?
            .ToString();
}
