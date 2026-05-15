namespace Haworks.BuildingBlocks.Authentication;

/// <summary>
/// Provides a cached service-to-service JWT for internal calls.
/// Implemented by the BFF to obtain tokens from Identity.
/// </summary>
public interface IServiceTokenProvider
{
    Task<string?> GetTokenAsync(CancellationToken ct = default);
}
