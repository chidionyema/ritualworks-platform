using System.IdentityModel.Tokens.Jwt;
using Haworks.Identity.Application.DTOs;
using Haworks.Identity.Application.Telemetry;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Haworks.Identity.Application;

public sealed record LoginCommand(
    string Username,
    string Password,
    HttpContext HttpContext,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result<AuthResponseDto>>;

internal sealed class LoginCommandHandler : IRequestHandler<LoginCommand, Result<AuthResponseDto>>
{
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly JwtOptions _jwtOptions;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<LoginCommandHandler> _logger;

    public LoginCommandHandler(
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        IJwtTokenService jwtTokenService,
        IRefreshTokenService refreshTokenService,
        IOptions<JwtOptions> jwtOptions,
        IAuditLogger auditLogger,
        ILogger<LoginCommandHandler> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwtTokenService = jwtTokenService;
        _refreshTokenService = refreshTokenService;
        _jwtOptions = jwtOptions.Value;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public async Task<Result<AuthResponseDto>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        using var activity = IdentityActivities.Source.StartActivity("identity.login");
        // Username is PII-light (not the password) and is the primary
        // axis ops want to slice login traces by. user.id is filled in
        // below once we resolve the user — gated on the lookup succeeding.
        activity?.SetTag("user.username", request.Username);

        _logger.LogInformation("Attempting to login user: {Username}", request.Username);

        var correlationId = request.HttpContext.GetCorrelationId();
        var ipAddress = request.HttpContext.GetClientIpAddress();
        var userAgent = request.HttpContext.GetUserAgent();

        var user = await _userManager.FindByNameAsync(request.Username);
        if (user == null)
        {
            _logger.LogWarning("User not found during login attempt: {Username}", request.Username);

            await _auditLogger.LogAsync(new AuditEvent
            {
                Action = AuditActions.LoginFailed,
                UserId = string.Empty,
                Resource = $"User:{request.Username}",
                IsSuccess = false,
                Details = "User not found",
                IpAddress = ipAddress,
                UserAgent = userAgent,
                CorrelationId = correlationId
            }, cancellationToken);

            return Result.Failure<AuthResponseDto>(Error.Auth.InvalidCredentials);
        }

        // Check if user is locked out
        if (await _userManager.IsLockedOutAsync(user))
        {
            var lockoutEnd = await _userManager.GetLockoutEndDateAsync(user);
            _logger.LogWarning(
                "User {Username} is locked out until {LockoutEnd}",
                request.Username, lockoutEnd);

            await _auditLogger.LogAsync(new AuditEvent
            {
                Action = AuditActions.LoginFailed,
                UserId = user.Id,
                Resource = $"User:{user.Id}",
                IsSuccess = false,
                Details = $"Account locked out until {lockoutEnd}",
                IpAddress = ipAddress,
                UserAgent = userAgent,
                CorrelationId = correlationId
            }, cancellationToken);

            return Result.Failure<AuthResponseDto>(
                Error.Auth.AccountLocked(AuthConstants.LockoutDurationMinutes));
        }

        // Use CheckPasswordSignInAsync to track failed attempts and handle lockout
        var signInResult = await _signInManager.CheckPasswordSignInAsync(
            user, request.Password, lockoutOnFailure: true);

        if (signInResult.IsLockedOut)
        {
            _logger.LogWarning(
                "User {Username} has been locked out after too many failed attempts",
                request.Username);

            await _auditLogger.LogAsync(new AuditEvent
            {
                Action = AuditActions.AccountLockout,
                UserId = user.Id,
                Resource = $"User:{user.Id}",
                IsSuccess = false,
                Details = $"Account locked out for {AuthConstants.LockoutDurationMinutes} minutes after {AuthConstants.MaxFailedLoginAttempts} failed attempts",
                IpAddress = ipAddress,
                UserAgent = userAgent,
                CorrelationId = correlationId
            }, cancellationToken);

            return Result.Failure<AuthResponseDto>(
                Error.Auth.AccountLocked(AuthConstants.LockoutDurationMinutes));
        }

        if (!signInResult.Succeeded)
        {
            var failedCount = await _userManager.GetAccessFailedCountAsync(user);
            var remainingAttempts = AuthConstants.MaxFailedLoginAttempts - failedCount;

            _logger.LogWarning(
                "Invalid password for user: {Username}. Failed attempts: {Failed}, Remaining: {Remaining}",
                request.Username, failedCount, remainingAttempts);

            await _auditLogger.LogAsync(new AuditEvent
            {
                Action = AuditActions.LoginFailed,
                UserId = user.Id,
                Resource = $"User:{user.Id}",
                IsSuccess = false,
                Details = $"Invalid password. Failed attempts: {failedCount}",
                IpAddress = ipAddress,
                UserAgent = userAgent,
                CorrelationId = correlationId
            }, cancellationToken);

            return Result.Failure<AuthResponseDto>(Error.Auth.InvalidCredentials);
        }

        // Reset failed access count on successful login
        await _userManager.ResetAccessFailedCountAsync(user);

        // Reject deactivated users even if credentials are valid
        if (!user.IsActive)
        {
            _logger.LogWarning("Login rejected for deactivated user: {Username}", request.Username);

            await _auditLogger.LogAsync(new AuditEvent
            {
                Action = AuditActions.LoginFailed,
                UserId = user.Id,
                Resource = $"User:{user.Id}",
                IsSuccess = false,
                Details = "Account is deactivated",
                IpAddress = ipAddress,
                UserAgent = userAgent,
                CorrelationId = correlationId
            }, cancellationToken);

            return Result.Failure<AuthResponseDto>(Error.Auth.AccountDeactivated);
        }

        activity?.SetTag("user.id", user.Id);
        activity?.SetTag("login.two_factor", user.TwoFactorEnabled);

        _logger.LogInformation("Login successful for user: {Username}, Id: {UserId}", user.UserName, user.Id);

        var token = await _jwtTokenService.GenerateTokenAsync(user, DateTime.UtcNow.AddMinutes(_jwtOptions.TokenExpiryMinutes), cancellationToken);
        var refreshTokenEntity = await _refreshTokenService.GenerateRefreshTokenAsync(user.Id, cancellationToken);
        _jwtTokenService.SetSecureCookie(request.HttpContext, token);

        await _auditLogger.LogAsync(new AuditEvent
        {
            Action = AuditActions.Login,
            UserId = user.Id,
            Resource = $"User:{user.Id}",
            IsSuccess = true,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CorrelationId = correlationId
        }, cancellationToken);

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
