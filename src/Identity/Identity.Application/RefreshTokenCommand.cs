using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Haworks.Identity.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.Identity.Application;

/// <summary>
/// Configuration for authentication token lifetimes.
/// </summary>
public sealed class AuthOptions
{
    public int TokenExpiryMinutes { get; init; } = 15;
}

/// <summary>
/// Command to refresh an expired access token using a valid refresh token.
/// </summary>
public sealed record RefreshTokenCommand(
    string AccessToken,
    string RefreshToken,
    HttpContext HttpContext
) : IRequest<Result<AuthResponseDto>>;

/// <summary>
/// Handler for token refresh - uses clean architecture interfaces.
/// </summary>
internal sealed class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, Result<AuthResponseDto>>
{
    private readonly UserManager<User> _userManager;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<RefreshTokenCommandHandler> _logger;
    private readonly AuthOptions _authOptions;

    public RefreshTokenCommandHandler(
        UserManager<User> userManager,
        IRefreshTokenRepository refreshTokenRepository,
        IRefreshTokenService refreshTokenService,
        IJwtTokenService jwtTokenService,
        IAuditLogger auditLogger,
        ILogger<RefreshTokenCommandHandler> logger,
        IOptions<AuthOptions> authOptions)
    {
        _userManager = userManager;
        _refreshTokenRepository = refreshTokenRepository;
        _refreshTokenService = refreshTokenService;
        _jwtTokenService = jwtTokenService;
        _auditLogger = auditLogger;
        _logger = logger;
        _authOptions = authOptions.Value;
    }

    public async Task<Result<AuthResponseDto>> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var correlationId = request.HttpContext?.GetCorrelationId() ?? Guid.NewGuid().ToString();
        var ipAddress = request.HttpContext?.GetClientIpAddress();
        var userAgent = request.HttpContext?.GetUserAgent();

        // 1. Basic Validation
        if (string.IsNullOrEmpty(request.AccessToken) || string.IsNullOrEmpty(request.RefreshToken))
            return Result.Failure<AuthResponseDto>(Error.Auth.MissingTokens);

        try
        {
            // 2. Validate Access Token (Ignore lifetime because it's expected to be expired)
            var principal = _jwtTokenService.ValidateToken(request.AccessToken, validateLifetime: false);
            if (principal == null)
                return Result.Failure<AuthResponseDto>(Error.Auth.InvalidAccessToken);

            // 3. Extract Identity
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
                         principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

            if (string.IsNullOrEmpty(userId))
                return Result.Failure<AuthResponseDto>(Error.Auth.MissingUserId);

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
                return Result.Failure<AuthResponseDto>(Error.Auth.UserNotFound);

            // Reject deactivated users — invalidate their session
            if (!user.IsActive)
            {
                _logger.LogWarning("Token refresh rejected for deactivated user {UserId}", userId);

                await _auditLogger.LogAsync(new AuditEvent
                {
                    Action = AuditActions.TokenRefreshFailed,
                    UserId = userId,
                    Resource = $"User:{userId}",
                    IsSuccess = false,
                    Details = "Account is deactivated",
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    CorrelationId = correlationId
                }, cancellationToken);

                return Result.Failure<AuthResponseDto>(Error.Auth.AccountDeactivated);
            }

            // 4. Atomic Token Rotation with transaction
            await using var transaction = (IAsyncDisposable)await _refreshTokenRepository.BeginTransactionAsync(cancellationToken);

            var storedToken = await _refreshTokenRepository.GetByTokenAndUserIdAsync(
                request.RefreshToken, userId, cancellationToken);

            if (storedToken == null)
            {
                _logger.LogWarning("Refresh attempt with non-existent token for user {UserId}", userId);

                await _auditLogger.LogAsync(new AuditEvent
                {
                    Action = AuditActions.TokenRefreshFailed,
                    UserId = userId,
                    Resource = $"User:{userId}",
                    IsSuccess = false,
                    Details = "Invalid refresh token",
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    CorrelationId = correlationId
                }, cancellationToken);

                return Result.Failure<AuthResponseDto>(Error.Auth.InvalidRefreshToken);
            }

            if (storedToken.Expires < DateTime.UtcNow)
            {
                _logger.LogWarning("Refresh attempt with expired token for user {UserId}", userId);
                await _refreshTokenRepository.RemoveAsync(storedToken, cancellationToken);
                await _refreshTokenRepository.SaveChangesAsync(cancellationToken);

                await _auditLogger.LogAsync(new AuditEvent
                {
                    Action = AuditActions.TokenRefreshFailed,
                    UserId = userId,
                    Resource = $"User:{userId}",
                    IsSuccess = false,
                    Details = "Expired refresh token",
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    CorrelationId = correlationId
                }, cancellationToken);

                return Result.Failure<AuthResponseDto>(Error.Auth.TokenExpired);
            }

            // 5. Revoke old and Issue new
            await _refreshTokenRepository.RemoveAsync(storedToken, cancellationToken);

            var expiry = DateTime.UtcNow.AddMinutes(_authOptions.TokenExpiryMinutes);
            var newAccessToken = await _jwtTokenService.GenerateTokenAsync(user, expiry);
            var newRefreshToken = await _refreshTokenService.GenerateRefreshTokenAsync(user.Id, cancellationToken);

            // 6. Update Secure Cookie
            if (request.HttpContext != null)
            {
                _jwtTokenService.SetSecureCookie(request.HttpContext, newAccessToken);
            }

            await _refreshTokenRepository.SaveChangesAsync(cancellationToken);
            await _refreshTokenRepository.CommitTransactionAsync(cancellationToken);

            _logger.LogInformation("Successfully rotated tokens for User {UserId}", userId);

            await _auditLogger.LogAsync(new AuditEvent
            {
                Action = AuditActions.TokenRefresh,
                UserId = userId,
                Resource = $"User:{userId}",
                IsSuccess = true,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                CorrelationId = correlationId
            }, cancellationToken);

            return Result.Success(new AuthResponseDto
            {
                Token = new JwtSecurityTokenHandler().WriteToken(newAccessToken),
                RefreshToken = newRefreshToken.Token,
                UserId = user.Id,
                Username = user.UserName,
                Email = user.Email,
                Expires = newAccessToken.ValidTo
            });
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning("SecurityTokenException during refresh: {Message}", ex.Message);
            return Result.Failure<AuthResponseDto>(Error.Auth.TokenProcessingError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in RefreshTokenHandler");
            return Result.Failure<AuthResponseDto>(Error.Auth.RefreshFailed);
        }
    }
}
