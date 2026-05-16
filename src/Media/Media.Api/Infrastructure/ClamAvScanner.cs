using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using nClam;

namespace Haworks.Media.Api.Infrastructure;

/// <summary>
/// Options for the ClamAV virus scanner.
/// Bound from the "ClamAV" configuration section.
/// </summary>
public sealed class ClamAvOptions
{
    public const string SectionName = "ClamAV";

    public string Host { get; set; } = "clamav";

    public int Port { get; set; } = 3310;

    public int TimeoutSeconds { get; set; } = 30;
}

public interface IVirusScanner
{
    /// <summary>
    /// Scans <paramref name="fileStream"/> for viruses via ClamAV.
    /// Returns true if clean, false if infected or the scan fails.
    /// </summary>
    Task<bool> ScanAsync(Stream fileStream, CancellationToken ct = default);
}

/// <summary>
/// Real ClamAV scanner via nClam TCP client.
/// Marks the file Quarantined before scanning; promotes to Active on a clean result.
/// Leaves it Quarantined (or transitions to Rejected) when infected.
/// </summary>
public sealed class ClamAvScanner : IVirusScanner
{
    private readonly ClamAvOptions _opts;
    private readonly ILogger<ClamAvScanner> _logger;

    public ClamAvScanner(IOptions<ClamAvOptions> opts, ILogger<ClamAvScanner> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<bool> ScanAsync(Stream fileStream, CancellationToken ct = default)
    {
        var clam = new ClamClient(_opts.Host, _opts.Port)
        {
            MaxStreamSize = 26_214_400, // 25 MB chunk limit
        };

        fileStream.Position = 0;
        ClamScanResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));
            result = await clam.SendAndScanFileAsync(fileStream, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClamAV scan failed — host={Host} port={Port}", _opts.Host, _opts.Port);
            // Fail closed: treat scan error as suspect
            return false;
        }

        switch (result.Result)
        {
            case ClamScanResults.Clean:
                _logger.LogInformation("ClamAV: clean");
                return true;

            case ClamScanResults.VirusDetected:
                _logger.LogWarning("ClamAV: virus detected — {Virus}", result.InfectedFiles?.FirstOrDefault()?.VirusName);
                return false;

            default:
                _logger.LogError("ClamAV: unexpected result {Result} — {Raw}", result.Result, result.RawResult);
                return false;
        }
    }
}
