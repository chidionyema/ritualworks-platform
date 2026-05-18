using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Haworks.Media.Api.Options;
using Microsoft.Extensions.Options;

namespace Haworks.Media.Api.Infrastructure.Processing;

public sealed class FfmpegService(IOptions<TranscodeOptions> opts, ILogger<FfmpegService> logger)
{
    private readonly TranscodeOptions _opts = opts.Value;

    public async Task<ProbeResult?> ProbeAsync(string inputPath, CancellationToken ct)
    {
        var argList = new[] { "-v", "quiet", "-print_format", "json", "-show_streams", "-show_format", inputPath };
        var (exitCode, stdout, _) = await RunAsync(_opts.FfprobePath, argList, TimeSpan.FromSeconds(30), ct);

        if (exitCode != 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            int? width = null, height = null;
            double? duration = null;
            string? videoCodec = null, audioCodec = null;
            bool hasVideo = false, hasAudio = false;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.GetProperty("codec_type").GetString();
                    if (string.Equals(codecType, "video", StringComparison.Ordinal) && !hasVideo)
                    {
                        hasVideo = true;
                        width = stream.GetProperty("width").GetInt32();
                        height = stream.GetProperty("height").GetInt32();
                        videoCodec = stream.GetProperty("codec_name").GetString();
                    }
                    else if (string.Equals(codecType, "audio", StringComparison.Ordinal) && !hasAudio)
                    {
                        hasAudio = true;
                        audioCodec = stream.GetProperty("codec_name").GetString();
                    }
                }
            }

            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var dur))
            {
                duration = double.Parse(dur.GetString()!, CultureInfo.InvariantCulture);
            }

            return new ProbeResult(width, height, duration, videoCodec, audioCodec, hasVideo, hasAudio);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse ffprobe output for {Path}", inputPath);
            return null;
        }
    }

    public async Task<string> TranscodeToHlsAsync(
        string inputPath, string outputDir, QualityTier tier, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var playlistPath = Path.Combine(outputDir, $"{tier.Name}.m3u8");
        var segmentPattern = Path.Combine(outputDir, $"{tier.Name}_%03d.ts");

        var argList = new[]
        {
            "-i", inputPath,
            "-vf", $"scale=-2:{tier.Height}",
            "-c:v", "libx264", "-preset", "medium", "-b:v", $"{tier.VideoBitrateKbps}k",
            "-c:a", "aac", "-b:a", "128k",
            "-hls_time", _opts.HlsSegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", "0",
            "-hls_segment_filename", segmentPattern,
            "-f", "hls", playlistPath,
        };

        var timeout = TimeSpan.FromMinutes(_opts.TimeoutMinutes);
        var (exitCode, _, stderr) = await RunAsync(_opts.FfmpegPath, argList, timeout, ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"FFmpeg transcode failed (exit {exitCode}): {stderr[..Math.Min(500, stderr.Length)]}");

        return playlistPath;
    }

    public async Task<string> NormalizeAudioAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        var argList = new[]
        {
            "-i", inputPath,
            "-af", "loudnorm=I=-16:LRA=11:TP=-1.5",
            "-c:a", "aac", "-b:a", "128k",
            outputPath,
        };

        var timeout = TimeSpan.FromMinutes(_opts.TimeoutMinutes);
        var (exitCode, _, stderr) = await RunAsync(_opts.FfmpegPath, argList, timeout, ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"FFmpeg audio normalization failed (exit {exitCode}): {stderr[..Math.Min(500, stderr.Length)]}");

        return outputPath;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string exe, string[] argList, TimeSpan timeout, CancellationToken ct)
    {
        logger.LogDebug("Running: {Exe} with {ArgCount} args", exe, argList.Length);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList prevents command injection — each arg is passed individually
        foreach (var arg in argList)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) { logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(RunAsync)); }
            throw new TimeoutException($"FFmpeg process timed out after {timeout.TotalMinutes}m");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}

public sealed record ProbeResult(
    int? Width, int? Height, double? DurationSeconds,
    string? VideoCodec, string? AudioCodec,
    bool HasVideo, bool HasAudio);
