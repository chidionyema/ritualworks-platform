using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Application.Models;
using Microsoft.Extensions.Logging;
using Refit;

namespace Haworks.Pricing.Infrastructure.Http;

/// <summary>
/// Refit interface for catalog API calls.
/// </summary>
internal interface IRefitCatalogClient
{
    [Get("/api/products/{id}")]
    Task<ApiResponse<CatalogProductDto>> GetProductAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Implementation of ICatalogPricingClient wrapping the Refit client.
/// </summary>
internal sealed class CatalogPricingHttpClient : ICatalogPricingClient
{
    private readonly IRefitCatalogClient _refitClient;
    private readonly ILogger<CatalogPricingHttpClient> _logger;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public CatalogPricingHttpClient(IRefitCatalogClient refitClient, ILogger<CatalogPricingHttpClient> logger)
    {
        _refitClient = refitClient;
        _logger = logger;
    }

    public async Task<CatalogProductResponse?> GetProductAsync(Guid id, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        try
        {
            var response = await _refitClient.GetProductAsync(id, timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && response.Content is not null)
            {
                return new CatalogProductResponse { IsSuccess = true, Product = response.Content };
            }

            _logger.LogWarning("Catalog returned {StatusCode} for product {ProductId}", response.StatusCode, id);
            return new CatalogProductResponse { IsSuccess = false };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch product {ProductId} from catalog", id);
            return null;
        }
    }
}
