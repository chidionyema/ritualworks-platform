using Haworks.BuildingBlocks.Vault;
using Haworks.Contracts.Secrets;
using Haworks.Identity.Application.Options;
using Haworks.Identity.Application.Services;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haworks.Identity.Application.Consumers;

/// <summary>
/// Handles <see cref="JwtKeyRotatedEvent"/> by re-fetching the new signing key
/// from Vault and configuring the dual-key overlap window.
/// No key material is logged or stored in the event.
/// </summary>
public sealed class JwtKeyRotatedConsumer : IConsumer<JwtKeyRotatedEvent>
{
    private readonly IVaultService _vault;
    private readonly DualKeyJwtValidator _dualKeyValidator;
    private readonly IJwtSigningKeyProvider _signingKeyProvider;
    private readonly IOptionsMonitor<JwtOptions> _jwtOptions;
    private readonly ILogger<JwtKeyRotatedConsumer> _logger;

    public JwtKeyRotatedConsumer(
        IVaultService vault,
        DualKeyJwtValidator dualKeyValidator,
        IJwtSigningKeyProvider signingKeyProvider,
        IOptionsMonitor<JwtOptions> jwtOptions,
        ILogger<JwtKeyRotatedConsumer> logger)
    {
        _vault = vault;
        _dualKeyValidator = dualKeyValidator;
        _signingKeyProvider = signingKeyProvider;
        _jwtOptions = jwtOptions;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<JwtKeyRotatedEvent> context)
    {
        var rotationId = context.Message.RotationId;
        _logger.LogInformation(
            "Processing JwtKeyRotatedEvent {RotationId}; triggering key refresh from Vault",
            rotationId);

        // Preserve the current key as the previous key for the overlap window
        var currentKey = _signingKeyProvider.SigningKey;
        _dualKeyValidator.SetPreviousKey(currentKey, _jwtOptions.CurrentValue.OverlapMinutes);

        // Force Vault service to refresh credentials — the RotatingJwtSigningKeyRing
        // will pick up the new key on its next poll cycle (every 30s).
        // We don't fetch the key directly here; the existing ring handles that.
        await _vault.RefreshCredentials("haworks-identity", context.CancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "JwtKeyRotatedEvent {RotationId} processed; overlap window active for {OverlapMinutes}m",
            rotationId, _jwtOptions.CurrentValue.OverlapMinutes);
    }
}
