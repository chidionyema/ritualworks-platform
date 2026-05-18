using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.BuildingBlocks.Middleware;

/// <summary>
/// Centralized rate limiting policies. Every service gets "default" via
/// ServiceDefaults. Services opt into stricter policies by name:
///   [EnableRateLimiting("auth")]    — strict auth endpoints (5/min per IP)
///   [EnableRateLimiting("user")]    — per-user partitioned (100/min)
///   [EnableRateLimiting("expensive")] — expensive ops (10/min per IP)
/// </summary>
public static class RateLimitingPolicies
{
    public const string Default = "default";
    public const string Auth = "auth";
    public const string User = "user";
    public const string Expensive = "expensive";

    public static IServiceCollection AddPlatformRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("RateLimiting");
        var defaultPermits = section.GetValue("DefaultPermitsPerWindow", 100);
        var authPermits = section.GetValue("AuthPermitsPerMinute", 5);
        var expensivePermits = section.GetValue("ExpensivePermitsPerMinute", 10);
        var userPermits = section.GetValue("UserPermitsPerMinute", 100);

        services.AddRateLimiter(opts =>
        {
            opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Default: fixed window per service (not per-IP, for internal services)
            opts.AddFixedWindowLimiter(Default, limiter =>
            {
                limiter.Window = TimeSpan.FromSeconds(10);
                limiter.PermitLimit = defaultPermits;
                limiter.QueueLimit = 10;
            });

            // Auth: strict per-IP limit for login/register (brute force protection)
            opts.AddPolicy(Auth, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetRemoteIp(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = authPermits,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // User: per-authenticated-user partitioning
            opts.AddPolicy(User, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetUserId(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = userPermits,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // Expensive: per-IP limit for costly operations
            opts.AddPolicy(Expensive, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetRemoteIp(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = expensivePermits,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        return services;
    }

    private static string GetRemoteIp(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string GetUserId(HttpContext ctx) =>
        ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? ctx.User.FindFirst("sub")?.Value
        ?? GetRemoteIp(ctx);
}
