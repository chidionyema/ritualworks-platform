using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Haworks.Identity.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Haworks.Identity.Application;

public sealed record RegisterCommand(
    string Username,
    string Email,
    string Password,
    HttpContext HttpContext,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result<AuthResponseDto>>;

internal sealed class RegisterCommandHandler : IRequestHandler<RegisterCommand, Result<AuthResponseDto>>
{
    private readonly UserManager<User> _userManager;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly JwtOptions _jwtOptions;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<RegisterCommandHandler> _logger;

    public RegisterCommandHandler(
        UserManager<User> userManager,
        IJwtTokenService jwtTokenService,
        IOptions<JwtOptions> jwtOptions,
        IAuditLogger auditLogger,
        ILogger<RegisterCommandHandler> logger)
    {
        _userManager = userManager;
        _jwtTokenService = jwtTokenService;
        _jwtOptions = jwtOptions.Value;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public async Task<Result<AuthResponseDto>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to register user: {Username}", request.Username);

        var correlationId = request.HttpContext.GetCorrelationId();
        var ipAddress = request.HttpContext.GetClientIpAddress();
        var userAgent = request.HttpContext.GetUserAgent();

        var user = new User
        {
            UserName = request.Username,
            Email = request.Email
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            foreach (var err in result.Errors)
            {
                _logger.LogWarning("Registration error for {Username}: {ErrorCode} - {ErrorDescription}",
                    request.Username, err.Code, err.Description);
            }

            var errorMessages = string.Join(", ", result.Errors.Select(e => e.Description));

            await _auditLogger.LogAsync(new AuditEvent
            {
                Action = AuditActions.RegisterFailed,
                UserId = string.Empty,
                Resource = $"User:{request.Username}",
                IsSuccess = false,
                Details = errorMessages,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                CorrelationId = correlationId
            }, cancellationToken);

            return Result.Failure<AuthResponseDto>(
                new Error("Auth.RegistrationFailed", errorMessages, ErrorType.Validation));
        }

        _logger.LogInformation("User registration succeeded for user: {Username}, Id: {UserId}",
            user.UserName, user.Id);

        // Wrap role + claim assignment in a transaction so they are atomic.
        using (var txScope = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            var roleResult = await _userManager.AddToRoleAsync(user, "ContentUploader");
            if (!roleResult.Succeeded)
            {
                _logger.LogError("Failed to add user {UserId} to role 'ContentUploader'. Errors: {Errors}",
                    user.Id, string.Join(", ", roleResult.Errors.Select(e => e.Description)));
                return Result.Failure<AuthResponseDto>(Error.Auth.RoleAssignmentFailed);
            }

            var claimResult = await _userManager.AddClaimAsync(user, new Claim("permission", "upload_content"));
            if (!claimResult.Succeeded)
            {
                _logger.LogError("Failed to add claim 'permission:upload_content' for user {UserId}. Errors: {Errors}",
                    user.Id, string.Join(", ", claimResult.Errors.Select(e => e.Description)));
                return Result.Failure<AuthResponseDto>(Error.Auth.ClaimAssignmentFailed);
            }

            txScope.Complete();
        }

        var token = await _jwtTokenService.GenerateTokenAsync(user, DateTime.UtcNow.AddMinutes(_jwtOptions.TokenExpiryMinutes), cancellationToken);
        _jwtTokenService.SetSecureCookie(request.HttpContext, token);

        await _auditLogger.LogAsync(new AuditEvent
        {
            Action = AuditActions.Register,
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
            UserId = user.Id,
            Username = user.UserName,
            Email = user.Email,
            Expires = token.ValidTo,
            Message = "Registration successful"
        });
    }
}
