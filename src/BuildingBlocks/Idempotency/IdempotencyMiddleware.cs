using System.Text.Json;
using Haworks.BuildingBlocks.CurrentUser;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Idempotency;

/// <summary>
/// HTTP-layer idempotency guard. Runs on every mutating request
/// (POST/PUT/PATCH/DELETE). When the caller supplies an
/// <c>X-Idempotency-Key</c> header the middleware atomically claims it
/// via <see cref="IIdempotencyStore"/> -- the first caller proceeds; any
/// caller after gets <c>409 Conflict</c>. Authenticated UserId is mixed
/// into the stored key so cross-user collisions are impossible.
///
/// Idempotency is opt-in (header-driven). Routes that don't need it
/// just don't send the header; routes that do retry-safe operations
/// can rely on the middleware to dedupe at ingress instead of writing
/// per-handler logic.
/// </summary>
public sealed class IdempotencyMiddleware
{
    private const string HeaderName = "X-Idempotency-Key";
    private const string TtlHeader = "X-Idempotency-Ttl-Seconds";
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24);
    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
    };

    private readonly RequestDelegate _next;

    public IdempotencyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        IIdempotencyStore store,
        ICurrentUserService currentUser,
        ILogger<IdempotencyMiddleware> logger)
    {
        if (!MutatingMethods.Contains(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var clientKey = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            // Idempotency is opt-in. No header -> not a retry-safe request.
            await _next(context);
            return;
        }

        // Server-side scope to UserId so two users with the same client-side
        // nonce can't collide. Falls back to a constant when unauthenticated
        // (anonymous endpoints still benefit from per-key dedup).
        var userScope = currentUser.UserId ?? "anonymous";
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        var storeKey = IdempotencyKey.Derive(userScope, path, clientKey);

        var ttl = ParseTtl(context.Request.Headers[TtlHeader].FirstOrDefault())
            ?? DefaultTtl;

        var claim = await store.TryClaimAsync(storeKey, ttl, context.RequestAborted);
        if (claim.IsDuplicate)
        {
            logger.LogInformation(
                "Idempotency dup: user={User} path={Path} clientKey={ClientKey}",
                userScope, path, clientKey);

            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.Headers["Idempotency-Status"] = "duplicate";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new
                {
                    error = "duplicate_request",
                    message = "An identical request is in flight or recently completed for this user. Wait or retry with a fresh idempotency key.",
                }),
                context.RequestAborted);
            return;
        }

        await _next(context);
    }

    private static TimeSpan? ParseTtl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value, out var seconds) || seconds <= 0) return null;
        var ttl = TimeSpan.FromSeconds(seconds);
        return ttl > MaxTtl ? MaxTtl : ttl;
    }
}
