using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.BuildingBlocks.Authentication;

public sealed class UserIdentityForwardingHandler : DelegatingHandler
{
    public const string HeaderName = "X-User-Id";
    private readonly IHttpContextAccessor _accessor;
    private readonly IServiceTokenProvider? _serviceTokenProvider;

    public UserIdentityForwardingHandler(
        IHttpContextAccessor accessor,
        IServiceProvider serviceProvider)
    {
        _accessor = accessor;
        _serviceTokenProvider = serviceProvider.GetService<IServiceTokenProvider>();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var httpContext = _accessor.HttpContext;
        var user = httpContext?.User;
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? user?.FindFirstValue("sub");

        if (!string.IsNullOrEmpty(userId) && !request.Headers.Contains(HeaderName))
            request.Headers.Add(HeaderName, userId);

        var authHeader = httpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && !request.Headers.Contains("Authorization"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }
        else if (_serviceTokenProvider != null && !request.Headers.Contains("Authorization"))
        {
            var serviceToken = await _serviceTokenProvider.GetTokenAsync(ct);
            if (!string.IsNullOrEmpty(serviceToken))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serviceToken}");
        }

        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
