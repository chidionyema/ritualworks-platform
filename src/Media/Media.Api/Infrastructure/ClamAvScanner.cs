using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using nClam;

namespace Haworks.Media.Api.Infrastructure;

public sealed class ClamAvOptions
{
    public const string SectionName = "ClamAV";

    public bool Enabled { get; set; } = true;

    [Required]
    public string Host { get; set; } = "clamav";

    [Range(1, 65535)]
    public int Port { get; set; } = 3310;

    [Range(5, 3600)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Max file size for in-memory INSTREAM scanning. Larger files use temp-file SCAN mode.</summary>
    public long InStreamMaxBytes { get; set; } = 25_000_000; // ~25 MB
}

public interface IVirusScanner
{
    Task<bool> ScanAsync(Stream fileStream, CancellationToken ct = default);

    /// <summary>
    /// Scans a file already on disk. Avoids copying to a second temp file
    /// when the caller already downloaded the content to a temp path.
    /// </summary>
    Task<bool> ScanFileAsync(string filePath, CancellationToken ct = default);
}

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
        if (!_opts.Enabled)
        {
            _logger.LogWarning("ClamAV disabled — skipping scan (UNSAFE in production)");
            return true;
        }

        fileStream.Position = 0;
        var fileSize = fileStream.Length;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

        // Large files: write to temp file, scan via MULTISCAN on filesystem
        if (fileSize > _opts.InStreamMaxBytes)
        {
            return await ScanViaFileSystemAsync(fileStream, cts.Token);
        }

        // Small files: stream directly via INSTREAM protocol
        return await ScanViaStreamAsync(fileStream, cts.Token);
    }

    public async Task<bool> ScanFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!_opts.Enabled)
        {
            _logger.LogWarning("ClamAV disabled — skipping scan (UNSAFE in production)");
            return true;
        }

        var fileSize = new FileInfo(filePath).Length;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

        // Small files: read into stream for INSTREAM protocol
        if (fileSize <= _opts.InStreamMaxBytes)
        {
            await using var stream = File.OpenRead(filePath);
            return await ScanViaStreamAsync(stream, cts.Token);
        }

        // Large files: scan directly on disk — no temp file copy
        var clam = new ClamClient(_opts.Host, _opts.Port);
        ClamScanResult result;
        try
        {
            result = await clam.ScanFileOnServerAsync(filePath, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClamAV filesystem scan failed for {Path}", filePath);
            return false; // fail closed
        }

        return InterpretResult(result);
    }

    private async Task<bool> ScanViaStreamAsync(Stream fileStream, CancellationToken ct)
    {
        var clam = new ClamClient(_opts.Host, _opts.Port)
        {
            MaxStreamSize = _opts.InStreamMaxBytes,
        };

        ClamScanResult result;
        try
        {
            result = await clam.SendAndScanFileAsync(fileStream, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClamAV INSTREAM scan failed — host={Host} port={Port}", _opts.Host, _opts.Port);
            return false; // fail closed
        }

        return InterpretResult(result);
    }

    private async Task<bool> ScanViaFileSystemAsync(Stream fileStream, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"clam-scan-{Guid.NewGuid()}");
        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await fileStream.CopyToAsync(fs, ct);
            }

            var clam = new ClamClient(_opts.Host, _opts.Port);
            ClamScanResult result;
            try
            {
                result = await clam.ScanFileOnServerAsync(tempPath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ClamAV filesystem scan failed for {Path}", tempPath);
                return false; // fail closed
            }

            return InterpretResult(result);
        }
        finally
        {
            try { File.Delete(tempPath); } catch (Exception ex) { _logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(ScanViaFileSystemAsync)); }
        }
    }

    private bool InterpretResult(ClamScanResult result)
    {
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
