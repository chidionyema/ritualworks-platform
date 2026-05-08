using Haworks.BuildingBlocks.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using System.Net;
using System.Net.Http.Json;

namespace Haworks.Search.Infrastructure.Catalog;

/// <summary>
/// Concrete <see cref="ICatalogProductsApi"/> using <see cref="HttpClient"/>
/// (injected by <see cref="IHttpClientFactory"/> via the typed-client overload
/// of <c>AddHttpClient</c>) and wrapping every call in the platform's
/// standard combined policy from <see cref="IResiliencePolicyFactory"/>.
///
/// This matches the established pattern in Stripe/PayPal services — wrap
/// at call site, not via Polly's <c>AddPolicyHandler</c>, because the
/// non-generic <c>IAsyncPolicy</c> doesn't fit the HTTP-handler signature.
/// </summary>
internal sealed class CatalogProductsApiClient : ICatalogProductsApi
{
    private readonly HttpClient _http;
    private readonly IAsyncPolicy _policy;
    private readonly ILogger<CatalogProductsApiClient> _logger;

    public CatalogProductsApiClient(
        HttpClient http,
        IResiliencePolicyFactory resiliencePolicyFactory,
        ILogger<CatalogProductsApiClient> logger)
    {
        _http = http;
        _policy = resiliencePolicyFactory.CreateCombinedPolicy(
            ResilienceOptions.ForExternalApi("catalog"));
        _logger = logger;
    }

    public async Task<CatalogProductDto> GetProductAsync(Guid id, CancellationToken ct = default)
    {
        return await _policy.ExecuteAsync(async innerCt =>
        {
            using var resp = await _http.GetAsync($"/api/products/{id}", innerCt).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var dto = await resp.Content.ReadFromJsonAsync<CatalogProductDto>(cancellationToken: innerCt).ConfigureAwait(false);
            return dto ?? throw new HttpRequestException($"Catalog returned null body for product {id}");
        }, ct).ConfigureAwait(false);
    }

    public async Task<CatalogProductPage> ListProductsAsync(int skip, int take, Guid? categoryId, CancellationToken ct = default)
    {
        return await _policy.ExecuteAsync(async innerCt =>
        {
            var query = $"/api/products?skip={skip}&take={take}";
            if (categoryId is { } cat) query += $"&categoryId={cat}";

            using var resp = await _http.GetAsync(query, innerCt).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var page = await resp.Content.ReadFromJsonAsync<CatalogProductPage>(cancellationToken: innerCt).ConfigureAwait(false);
            return page ?? throw new HttpRequestException("Catalog returned null body for product list");
        }, ct).ConfigureAwait(false);
    }
}
