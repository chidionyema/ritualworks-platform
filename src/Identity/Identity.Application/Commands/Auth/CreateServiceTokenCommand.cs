using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Identity.Application.Interfaces;
using MediatR;

namespace Haworks.Identity.Application.Commands.Auth;

/// <summary>
/// Mints a short-lived JWT for service-to-service calls (e.g. BFF -> backend).
/// Token is signed with the same RSA key used for user tokens so downstream
/// services validate it via the existing JWKS endpoint without extra config.
/// </summary>
public sealed record CreateServiceTokenCommand(string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<string>>;

internal sealed class CreateServiceTokenCommandHandler
    : IRequestHandler<CreateServiceTokenCommand, Result<string>>
{
    private const int ExpiryMinutes = 30;

    private readonly IJwtTokenService _jwtTokenService;

    public CreateServiceTokenCommandHandler(
        IJwtTokenService jwtTokenService)
    {
        _jwtTokenService = jwtTokenService;
    }

    public async Task<Result<string>> Handle(CreateServiceTokenCommand request, CancellationToken cancellationToken)
    {
        var expiry = DateTime.UtcNow.AddMinutes(ExpiryMinutes);
        var tokenString = await _jwtTokenService.GenerateServiceTokenAsync(expiry);
        return Result.Success(tokenString);
    }
}
