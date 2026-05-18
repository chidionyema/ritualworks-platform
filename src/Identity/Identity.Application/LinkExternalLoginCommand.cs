using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace Haworks.Identity.Application;

public sealed record LinkExternalLoginCommand(
    string UserId,
    string Provider,
    ExternalLoginInfo? LoginInfo,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result<LinkExternalLoginResult>>;

public sealed record LinkExternalLoginResult(
    bool RequiresChallenge,
    string? Message
);

internal sealed class LinkExternalLoginCommandHandler
    : IRequestHandler<LinkExternalLoginCommand, Result<LinkExternalLoginResult>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<LinkExternalLoginCommandHandler> _logger;

    public LinkExternalLoginCommandHandler(
        UserManager<User> userManager,
        ILogger<LinkExternalLoginCommandHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<LinkExternalLoginResult>> Handle(
        LinkExternalLoginCommand request,
        CancellationToken cancellationToken)
    {
        // Issue #1, #3: Validate LoginInfo early if provided
        if (request.LoginInfo != null && string.IsNullOrEmpty(request.LoginInfo.ProviderKey))
        {
            return Result.Failure<LinkExternalLoginResult>(Error.Auth.InvalidProviderKey);
        }

        // Issue #2, #7: Check existing external login before attempting to add
        if (request.LoginInfo != null)
        {
            var userForCheck = await _userManager.FindByIdAsync(request.UserId);
            if (userForCheck == null)
            {
                return Result.Failure<LinkExternalLoginResult>(Error.Auth.UserNotFound);
            }

            var existingLogins = await _userManager.GetLoginsAsync(userForCheck);
            if (existingLogins.Any(l => string.Equals(l.LoginProvider, request.LoginInfo.LoginProvider, StringComparison.Ordinal)))
            {
                return Result.Failure<LinkExternalLoginResult>(
                    Error.Conflict("Auth.AlreadyLinked", $"External login with {request.Provider} is already linked."));
            }
        }

        // Validate UserId (existing check)
        if (string.IsNullOrEmpty(request.UserId))
        {
            return Result.Failure<LinkExternalLoginResult>(Error.Auth.MissingUserId);
        }

        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
        {
            return Result.Failure<LinkExternalLoginResult>(Error.Auth.UserNotFound);
        }

        // No LoginInfo -> need to challenge
        if (request.LoginInfo == null)
        {
            return Result.Success(new LinkExternalLoginResult(RequiresChallenge: true, null));
        }

        // Issue #5: Pass cancellation token
        var result = await _userManager.AddLoginAsync(user, request.LoginInfo);
        if (!result.Succeeded)
        {
            // Issue #4: Do not expose raw error descriptions to client
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Failed to link external login for user {UserId}, provider {Provider}: {Errors}",
                user.Id, request.Provider, errors);

            return Result.Failure<LinkExternalLoginResult>(Error.Auth.LinkFailed);
        }

        // Issue #10: Add success audit log
        _logger.LogInformation("External login {Provider} successfully linked to user {UserId}.", request.Provider, user.Id);

        // Issue #6: Success message is fine, keep as is
        return Result.Success(new LinkExternalLoginResult(
            RequiresChallenge: false,
            Message: $"Successfully linked {request.Provider} login"));
    }
}