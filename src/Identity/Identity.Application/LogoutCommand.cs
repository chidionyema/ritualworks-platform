using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.Identity.Application;

public sealed record LogoutCommand(
    ClaimsPrincipal User,
    HttpContext HttpContext
) : IRequest<Result>;

internal sealed class LogoutCommandHandler : IRequestHandler<LogoutCommand, Result>
{
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ITokenRevocationService _tokenRevocationService;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<LogoutCommandHandler> _logger;

    public LogoutCommandHandler(
        IJwtTokenService jwtTokenService,
        ITokenRevocationService tokenRevocationService,
        IRefreshTokenRepository refreshTokenRepository,
        IAuditLogger auditLogger,
        ILogger<LogoutCommandHandler> logger)
    {
        _jwtTokenService = jwtTokenService;
        _tokenRevocationService = tokenRevocationService;
        _refreshTokenRepository = refreshTokenRepository;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public async Task<Result> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[Logout] Method entered. User Authenticated: {IsAuthenticated}. AuthenticationType: {AuthType}",
            request.User.Identity?.IsAuthenticated,
            request.User.Identity?.AuthenticationType);

        var userId = request.User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("Logout called for User ID: {UserId}", userId ?? "Unknown (claim not found)");

        var jti = request.User.FindFirstValue(JwtRegisteredClaimNames.Jti);
        var expiryClaim = request.User.FindFirstValue(JwtRegisteredClaimNames.Exp);

        if (!string.IsNullOrEmpty(jti) && !string.IsNullOrEmpty(userId) &&
            !string.IsNullOrEmpty(expiryClaim) && long.TryParse(expiryClaim, out long expiryUnixTime))
        {
            var expiryDate = DateTimeOffset.FromUnixTimeSeconds(expiryUnixTime).UtcDateTime;
            await _tokenRevocationService.RevokeTokenAsync(jti, userId, expiryDate, cancellationToken);
            _logger.LogInformation("Access token (JTI: {Jti}) for User ID: {UserId} marked as revoked.", jti, userId);
        }
        else
        {
            _logger.LogWarning("Could not revoke token for User ID: {UserId} due to missing JTI, UserID, or Expiry claim.",
                userId ?? "Unknown");
        }

        if (!string.IsNullOrEmpty(userId))
        {
            // Known limitation: access token revocation (above) and refresh
            // token removal are not in a single transaction because they use
            // separate persistence stores (RevokedToken table via
            // TokenRevocationService vs RefreshToken table). If this call
            // fails, the access token is already revoked (safe side) and
            // refresh tokens remain — the short access-token TTL limits the
            // exposure window. A retry will clean them up.
            await _refreshTokenRepository.RemoveAllForUserAsync(userId, cancellationToken);
            await _refreshTokenRepository.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Revoked all refresh tokens for user {UserId}", userId);
        }

        _jwtTokenService.DeleteAuthCookie(request.HttpContext);

        // Log audit event for logout
        var correlationId = request.HttpContext?.GetCorrelationId() ?? Guid.NewGuid().ToString();
        var ipAddress = request.HttpContext?.GetClientIpAddress();
        var userAgent = request.HttpContext?.GetUserAgent();

        await _auditLogger.LogAsync(new AuditEvent
        {
            Action = AuditActions.Logout,
            UserId = userId ?? string.Empty,
            Resource = $"User:{userId ?? "unknown"}",
            IsSuccess = true,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CorrelationId = correlationId
        }, cancellationToken);

        return Result.Success();
    }
}
