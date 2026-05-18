using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace Haworks.Identity.Application;

public sealed record UnlinkExternalLoginCommand(
    string UserId,
    string Provider,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result>;

internal sealed class UnlinkExternalLoginCommandHandler : IRequestHandler<UnlinkExternalLoginCommand, Result>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<UnlinkExternalLoginCommandHandler> _logger;

    public UnlinkExternalLoginCommandHandler(
        UserManager<User> userManager,
        ILogger<UnlinkExternalLoginCommandHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result> Handle(UnlinkExternalLoginCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.UserId))
        {
            return Result.Failure(Error.Auth.MissingUserId);
        }

        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
        {
            return Result.Failure(Error.Auth.UserNotFound);
        }

        var logins = await _userManager.GetLoginsAsync(user);
        var loginToRemove = logins.FirstOrDefault(l => string.Equals(l.LoginProvider, request.Provider, StringComparison.Ordinal));
        if (loginToRemove == null)
        {
            return Result.Failure(Error.Auth.LoginNotFound);
        }

        var result = await _userManager.RemoveLoginAsync(user, loginToRemove.LoginProvider, loginToRemove.ProviderKey);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Failed to unlink external login: {Errors}", errors);
            return Result.Failure(Error.Auth.UnlinkFailed);
        }

        _logger.LogInformation("Removed {Provider} login from user {UserId}", request.Provider, request.UserId);
        return Result.Success();
    }
}
