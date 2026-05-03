using System.Text.Json;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Infrastructure.Options;
using Haworks.Content.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace Haworks.Content.Infrastructure.ExternalServices.Validation;

public class ClamAVScanner : IVirusScanner
{
    private readonly HttpClient _httpClient;
    private readonly ClamAvOptions _options;

    public ClamAVScanner(IOptions<ClamAvOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<VirusScanResult> ScanAsync(Stream fileStream)
    {
        fileStream.Position = 0;
        using var content = new StreamContent(fileStream);
        var response = await _httpClient.PostAsync(_options.RestApiUrl, content);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ClamAV scan failed with status code: {response.StatusCode}");
        }

        var result = await JsonSerializer.DeserializeAsync<ClamAVResponse>(
            await response.Content.ReadAsStreamAsync()
        );

        return new VirusScanResult(
            result!.IsMalicious,
            result.VirusName
        );
    }

    private sealed class ClamAVResponse
    {
        public bool IsMalicious { get; set; }
        public string? VirusName { get; set; }
    }
}
