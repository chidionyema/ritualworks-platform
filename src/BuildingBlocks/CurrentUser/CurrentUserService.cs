using System.Security.Claims;
using Haworks.BuildingBlocks.Extensions;
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
        _contextAccessor.HttpContext?.GetForwardedUserId()
        ?? _contextAccessor.HttpContext?.User?
            .FindFirstValue(ClaimTypes.NameIdentifier);

    public string? ClientIp =>
        _contextAccessor.HttpContext?.Connection.RemoteIpAddress?
            .ToString();
}
