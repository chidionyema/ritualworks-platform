using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Haworks.Identity.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Haworks.Identity.Application;

public sealed record ExternalLoginCallbackCommand(
    HttpContext HttpContext
) : IRequest<Result<AuthResponseDto>>;

/// <summary>
/// Options for configuring external login behavior.
/// </summary>
public sealed class ExternalLoginOptions
{
    /// <summary>
    /// Providers that are trusted to have verified emails (e.g., Google, Microsoft).
    /// For these providers, we trust the email claim without requiring email_verified.
    /// </summary>
    public string[] TrustedEmailProviders { get; init; } = ["Google", "Microsoft", "Apple"];

    /// <summary>
    /// Characters allowed in usernames.
    /// </summary>
    public string AllowedUserNameCharacters { get; init; } = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";

    /// <summary>
    /// Maximum username length.
    /// </summary>
    public int MaxUserNameLength { get; init; } = 50;
}

internal sealed class ExternalLoginCallbackCommandHandler
    : IRequestHandler<ExternalLoginCallbackCommand, Result<AuthResponseDto>>
{
    private readonly SignInManager<User> _signInManager;
    private readonly UserManager<User> _userManager;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly ILogger<ExternalLoginCallbackCommandHandler> _logger;
    private readonly ExternalLoginOptions _options;

    public ExternalLoginCallbackCommandHandler(
        SignInManager<User> signInManager,
        UserManager<User> userManager,
        IJwtTokenService jwtTokenService,
        IRefreshTokenService refreshTokenService,
        ILogger<ExternalLoginCallbackCommandHandler> logger,
        IOptions<ExternalLoginOptions>? options = null)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _jwtTokenService = jwtTokenService;
        _refreshTokenService = refreshTokenService;
        _logger = logger;
        _options = options?.Value ?? new ExternalLoginOptions();
    }

    public async Task<Result<AuthResponseDto>> Handle(
        ExternalLoginCallbackCommand request,
        CancellationToken cancellationToken)
    {
        // -----------------------------------------------------------------
        // 1. Validate inputs
        // -----------------------------------------------------------------
        if (request.HttpContext == null)
            return Result.Failure<AuthResponseDto>(Error.Auth.InvalidContext);

        var loginInfo = await _signInManager.GetExternalLoginInfoAsync();
        if (loginInfo == null)
            return Result.Failure<AuthResponseDto>(Error.Auth.ExternalLoginFailed);

        if (string.IsNullOrWhiteSpace(loginInfo.ProviderKey))
            return Result.Failure<AuthResponseDto>(Error.Auth.InvalidProviderKey);

        _logger.LogInformation("Processing external login callback. Provider: {Provider}", loginInfo.LoginProvider);

        // -----------------------------------------------------------------
        // 2. Try existing external login sign-in
        // -----------------------------------------------------------------
        var signInResult = await _signInManager.ExternalLoginSignInAsync(
            loginInfo.LoginProvider,
            loginInfo.ProviderKey,
            isPersistent: false,
            bypassTwoFactor: false);

        if (signInResult.Succeeded)
        {
            var user = await _userManager.FindByLoginAsync(
                loginInfo.LoginProvider, loginInfo.ProviderKey);

            if (user == null)
            {
                _logger.LogError("Inconsistency: ExternalLoginSignIn succeeded but FindByLoginAsync returned null.");
                return Result.Failure<AuthResponseDto>(Error.Auth.UserInconsistency);
            }

            _logger.LogInformation("User {UserId} signed in via external provider {Provider}.", user.Id, loginInfo.LoginProvider);
            return await GenerateAuthResponseAsync(user, request.HttpContext, cancellationToken);
        }

        // -----------------------------------------------------------------
        // 3. Extract and validate email claim
        // -----------------------------------------------------------------
        var email = loginInfo.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogWarning("External provider {Provider} did not return an email claim.", loginInfo.LoginProvider);
            return Result.Failure<AuthResponseDto>(Error.Auth.MissingEmail);
        }

        // -----------------------------------------------------------------
        // 4. SECURITY: Verify email is verified by the provider before linking
        //    This prevents account hijacking via unverified email providers.
        // -----------------------------------------------------------------
        if (!IsEmailVerified(loginInfo))
        {
            _logger.LogWarning(
                "External provider {Provider} returned unverified email {Email}. Blocking account linking.",
                loginInfo.LoginProvider, email);
            return Result.Failure<AuthResponseDto>(Error.Auth.UnverifiedEmail);
        }

        // -----------------------------------------------------------------
        // 5. Check if this external login is already linked to any user
        // -----------------------------------------------------------------
        var existingLoginUser = await _userManager.FindByLoginAsync(
            loginInfo.LoginProvider, loginInfo.ProviderKey);
        if (existingLoginUser != null)
        {
            _logger.LogWarning("External login {Provider} already linked to user {UserId}.",
                loginInfo.LoginProvider, existingLoginUser.Id);
            return Result.Failure<AuthResponseDto>(Error.Auth.AlreadyLinked);
        }

        // -----------------------------------------------------------------
        // 6. Try to find existing user by email and link the login
        // -----------------------------------------------------------------
        var userByEmail = await _userManager.FindByEmailAsync(email);
        if (userByEmail != null)
        {
            var linkResult = await _userManager.AddLoginAsync(userByEmail, loginInfo);
            if (!linkResult.Succeeded)
            {
                var errors = string.Join(", ", linkResult.Errors.Select(e => e.Description));
                _logger.LogWarning("Failed to link external login to existing user {UserId}: {Errors}",
                    userByEmail.Id, errors);
                return Result.Failure<AuthResponseDto>(Error.Auth.LinkFailed);
            }

            _logger.LogInformation("Linked external login {Provider} to existing user {UserId}.",
                loginInfo.LoginProvider, userByEmail.Id);
            return await GenerateAuthResponseAsync(userByEmail, request.HttpContext, cancellationToken);
        }

        // -----------------------------------------------------------------
        // 7. Create a new user account (with race-condition handling)
        // -----------------------------------------------------------------
        var rawUserName = loginInfo.Principal.FindFirstValue(ClaimTypes.Name) ?? email.Split('@')[0];
        var sanitizedUserName = SanitizeUserName(rawUserName);
        var uniqueUserName = await GetUniqueUserNameAsync(sanitizedUserName);

        var newUser = new User
        {
            UserName = uniqueUserName,
            Email = email,
            EmailConfirmed = true // Verified by external provider
        };

        // Wrap all Identity writes in a TransactionScope so they commit or roll back atomically.
        using (var txScope = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            var createResult = await _userManager.CreateAsync(newUser);
            if (!createResult.Succeeded)
            {
                return await HandleCreationFailureAsync(createResult, email, loginInfo, request.HttpContext, cancellationToken);
            }

            var linkNewUserResult = await _userManager.AddLoginAsync(newUser, loginInfo);
            if (!linkNewUserResult.Succeeded)
            {
                // TransactionScope disposes without Complete = auto-rollback.
                _logger.LogWarning("Failed to link external login to new user {Email}. Rolling back.", email);
                return Result.Failure<AuthResponseDto>(Error.Auth.LinkFailed);
            }

            var roleResult = await _userManager.AddToRoleAsync(newUser, "ContentUploader");
            if (!roleResult.Succeeded)
            {
                _logger.LogWarning("Failed to assign role to new user {UserId}: {Errors}",
                    newUser.Id, string.Join(", ", roleResult.Errors.Select(e => e.Description)));
            }

            var claimResult = await _userManager.AddClaimAsync(newUser, new Claim("permission", "upload_content"));
            if (!claimResult.Succeeded)
            {
                _logger.LogWarning("Failed to assign claim to new user {UserId}: {Errors}",
                    newUser.Id, string.Join(", ", claimResult.Errors.Select(e => e.Description)));
            }

            txScope.Complete();
        }

        _logger.LogInformation("Created new user {UserId} ({Email}) from external login {Provider}.",
            newUser.Id, email, loginInfo.LoginProvider);
        return await GenerateAuthResponseAsync(newUser, request.HttpContext, cancellationToken);
    }

    // -----------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Checks if the email has been verified by the external provider.
    /// This is critical for preventing account hijacking attacks.
    /// </summary>
    private bool IsEmailVerified(ExternalLoginInfo loginInfo)
    {
        // Check for standard email_verified claim
        var emailVerifiedClaim = loginInfo.Principal.FindFirst("email_verified")
            ?? loginInfo.Principal.FindFirst("verified_email");

        if (emailVerifiedClaim != null &&
            emailVerifiedClaim.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Trusted providers (Google, Microsoft, Apple) verify emails as part of their auth flow
        if (_options.TrustedEmailProviders.Contains(loginInfo.LoginProvider, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // For other providers, require explicit email_verified claim
        return false;
    }

    /// <summary>
    /// Sanitizes the username to only allow permitted characters.
    /// </summary>
    private string SanitizeUserName(string rawUserName)
    {
        if (string.IsNullOrWhiteSpace(rawUserName))
            return "user";

        // Remove any characters not in the allowed set
        var pattern = $"[^{Regex.Escape(_options.AllowedUserNameCharacters)}]";
        var sanitized = Regex.Replace(rawUserName, pattern, "", RegexOptions.NonBacktracking);

        // Ensure we have at least something
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "user";

        // Truncate to max length
        if (sanitized.Length > _options.MaxUserNameLength)
            sanitized = sanitized[.._options.MaxUserNameLength];

        return sanitized;
    }

    /// <summary>
    /// Generates a unique username by appending numbers or a GUID suffix.
    /// </summary>
    private async Task<string> GetUniqueUserNameAsync(string baseName)
    {
        var userName = baseName;
        var counter = 1;

        while (await _userManager.FindByNameAsync(userName) != null)
        {
            userName = counter > 100
                ? $"{baseName}_{Guid.NewGuid():N}"[..Math.Min(20, _options.MaxUserNameLength)]
                : $"{baseName}{counter++}";

            // Ensure we don't exceed max length
            if (userName.Length > _options.MaxUserNameLength)
                userName = userName[.._options.MaxUserNameLength];
        }

        return userName;
    }

    /// <summary>
    /// Handles user creation failure with proper race condition detection.
    /// Distinguishes between duplicate email vs duplicate username failures.
    /// </summary>
    private async Task<Result<AuthResponseDto>> HandleCreationFailureAsync(
        IdentityResult createResult,
        string email,
        ExternalLoginInfo loginInfo,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var errors = createResult.Errors.ToList();
        var hasDuplicateEmail = errors.Any(e => string.Equals(e.Code, "DuplicateEmail", StringComparison.Ordinal));
        var hasDuplicateUserName = errors.Any(e => string.Equals(e.Code, "DuplicateUserName", StringComparison.Ordinal));

        _logger.LogWarning(
            "User creation failed. DuplicateEmail: {DuplicateEmail}, DuplicateUserName: {DuplicateUserName}, Errors: {Errors}",
            hasDuplicateEmail, hasDuplicateUserName,
            string.Join(", ", errors.Select(e => $"{e.Code}: {e.Description}")));

        // Handle duplicate email (race condition - another request created the user)
        if (hasDuplicateEmail)
        {
            var retryUser = await _userManager.FindByEmailAsync(email);
            if (retryUser != null)
            {
                _logger.LogWarning(
                    "Race condition detected: User with email {Email} was created by another request. Attempting to link external login.",
                    email);

                var retryLink = await _userManager.AddLoginAsync(retryUser, loginInfo);
                if (retryLink.Succeeded)
                {
                    _logger.LogInformation(
                        "Successfully linked external login {Provider} to existing user {UserId} after race condition.",
                        loginInfo.LoginProvider, retryUser.Id);
                    return await GenerateAuthResponseAsync(retryUser, httpContext, cancellationToken);
                }

                _logger.LogWarning("Failed to link external login after race condition: {Errors}",
                    string.Join(", ", retryLink.Errors.Select(e => e.Description)));
            }
        }

        // Handle duplicate username race condition — regenerate unique name and retry
        if (hasDuplicateUserName && !hasDuplicateEmail)
        {
            _logger.LogWarning(
                "Duplicate username collision for email {Email}. Retrying with new unique name.",
                email);

            var rawUserName = loginInfo.Principal.FindFirstValue(ClaimTypes.Name) ?? email.Split('@')[0];
            var retryUserName = await GetUniqueUserNameAsync(SanitizeUserName(rawUserName));
            var retryUser = new User
            {
                UserName = retryUserName,
                Email = email,
                EmailConfirmed = true
            };

            var retryCreate = await _userManager.CreateAsync(retryUser);
            if (retryCreate.Succeeded)
            {
                var retryLink = await _userManager.AddLoginAsync(retryUser, loginInfo);
                if (retryLink.Succeeded)
                {
                    _logger.LogInformation(
                        "Created user {UserId} ({Email}) on username retry via {Provider}.",
                        retryUser.Id, email, loginInfo.LoginProvider);
                    return await GenerateAuthResponseAsync(retryUser, httpContext, cancellationToken);
                }

                await _userManager.DeleteAsync(retryUser);
                _logger.LogWarning("Failed to link external login after username retry for {Email}.", email);
            }
        }

        return Result.Failure<AuthResponseDto>(Error.Auth.CreateFailed);
    }

    private async Task<Result<AuthResponseDto>> GenerateAuthResponseAsync(
        User user,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var token = await _jwtTokenService.GenerateTokenAsync(user, DateTime.UtcNow.AddMinutes(15));
        var refreshTokenEntity = await _refreshTokenService.GenerateRefreshTokenAsync(user.Id, cancellationToken);
        _jwtTokenService.SetSecureCookie(httpContext, token);

        return Result.Success(new AuthResponseDto
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            RefreshToken = refreshTokenEntity.Token,
            UserId = user.Id,
            Username = user.UserName,
            Email = user.Email,
            Expires = token.ValidTo
        });
    }
}
