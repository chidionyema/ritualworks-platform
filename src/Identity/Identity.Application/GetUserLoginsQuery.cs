using Haworks.BuildingBlocks.Common;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace Haworks.Identity.Application;

public sealed record GetUserLoginsQuery(string UserId) : IRequest<Result<List<ExternalLoginInfoDto>>>;

public sealed record ExternalLoginInfoDto(string Provider, string? ProviderDisplayName);

internal sealed class GetUserLoginsQueryHandler : IRequestHandler<GetUserLoginsQuery, Result<List<ExternalLoginInfoDto>>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<GetUserLoginsQueryHandler> _logger;

    public GetUserLoginsQueryHandler(
        UserManager<User> userManager,
        ILogger<GetUserLoginsQueryHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<List<ExternalLoginInfoDto>>> Handle(
        GetUserLoginsQuery request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("GetUserLogins called for user ID: {UserId}", request.UserId);

        if (string.IsNullOrEmpty(request.UserId))
        {
            _logger.LogWarning("User identifier not found in GetUserLogins");
            return Result.Failure<List<ExternalLoginInfoDto>>(
                Error.Validation("Auth.MissingUserId", "User identifier not found"));
        }

        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
        {
            _logger.LogWarning("User not found in GetUserLogins for ID: {UserId}", request.UserId);
            return Result.Failure<List<ExternalLoginInfoDto>>(
                Error.NotFound("Auth.UserNotFound", "User not found"));
        }

        var logins = await _userManager.GetLoginsAsync(user);
        _logger.LogInformation("Found {Count} logins for user {UserId}", logins.Count, request.UserId);

        var result = logins.Select(l => new ExternalLoginInfoDto(l.LoginProvider, l.ProviderDisplayName)).ToList();
        return Result.Success(result);
    }
}
