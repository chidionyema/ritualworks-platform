using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Testing;

public class TestAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TestAuthMiddleware> _logger;

    public TestAuthMiddleware(RequestDelegate next, ILogger<TestAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-user-id"),
                new Claim(ClaimTypes.Name, "test_auth_user"),
                new Claim(ClaimTypes.Role, "ContentUploader"),
                new Claim("permission", "upload_content"),
            };

            var identity = new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            context.User = principal;

            _logger.LogDebug("TestAuthMiddleware: Added test claims to request");
        }

        return _next(context);
    }
}

public static class TestAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseTestAuth(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TestAuthMiddleware>();
    }

    public static WebApplicationFactory<TEntryPoint> WithTestAuth<TEntryPoint>(this WebApplicationFactory<TEntryPoint> factory) where TEntryPoint : class
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.Configure(app =>
            {
                app.UseRouting();
                app.UseTestAuth();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                });
            });
        });
    }
}
