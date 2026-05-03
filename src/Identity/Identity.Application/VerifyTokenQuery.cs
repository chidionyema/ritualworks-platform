using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Haworks.Identity.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace Haworks.Identity.Application;

public sealed record VerifyTokenQuery(ClaimsPrincipal User) : IRequest<Result<TokenVerificationDto>>;

internal sealed class VerifyTokenQueryHandler : IRequestHandler<VerifyTokenQuery, Result<TokenVerificationDto>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<VerifyTokenQueryHandler> _logger;

    public VerifyTokenQueryHandler(
        UserManager<User> userManager,
        ILogger<VerifyTokenQueryHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<TokenVerificationDto>> Handle(VerifyTokenQuery request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[VerifyToken] Method entered. User Authenticated: {IsAuthenticated}. AuthenticationType: {AuthType}",
            request.User.Identity?.IsAuthenticated,
            request.User.Identity?.AuthenticationType);

        string? userId = request.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                        request.User.FindFirstValue(JwtRegisteredClaimNames.Sub);

        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("[VerifyToken] Authorized user but no userId claim (NameIdentifier or Sub) found. Claims: {Claims}",
                string.Join(", ", request.User.Claims.Select(c => $"{c.Type}={c.Value}")));

            // Attempt fallback if Identity.Name is available
            var identityName = request.User.Identity?.Name;
            if (!string.IsNullOrEmpty(identityName))
            {
                var userByName = await _userManager.FindByNameAsync(identityName);
                if (userByName != null)
                {
                    userId = userByName.Id;
                    _logger.LogInformation("[VerifyToken] User ID resolved via Identity.Name to {UserId}", userId);
                }
            }

            if (string.IsNullOrEmpty(userId))
            {
                return Result.Success(new TokenVerificationDto
                {
                    UserId = string.Empty,
                    UserName = request.User.Identity?.Name,
                    IsAuthenticated = true,
                    Message = "Token is valid, but user identifier could not be resolved from claims."
                });
            }
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            _logger.LogWarning("[VerifyToken] User ID '{UserId}' from token not found in database. Token might be for a deleted user.", userId);
            return Result.Failure<TokenVerificationDto>(
                Error.Conflict("Auth.UserDeleted", "Token is valid, but the associated user account no longer exists."));
        }

        _logger.LogInformation("[VerifyToken] Token verified for User ID: {UserId}, Username: {Username}", user.Id, user.UserName);
        return Result.Success(new TokenVerificationDto
        {
            UserId = user.Id,
            UserName = user.UserName,
            IsAuthenticated = true
        });
    }
}
