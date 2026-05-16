using Haworks.Contracts.Media;
using Haworks.Media.Api.Options;
using Microsoft.Extensions.Options;

namespace Haworks.Media.Api.Infrastructure.Processing;

public sealed class AudioProcessor(
    FfmpegService ffmpeg,
    IS3Service s3,
    IOptions<TranscodeOptions> opts,
    ILogger<AudioProcessor> logger) : IMediaProcessor
{
    private readonly TranscodeOptions _opts = opts.Value;

    public bool CanProcess(string mimeType) =>
        _opts.Enabled && mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<MediaVariant>> ProcessAsync(
        Guid mediaId, string s3Key, string mimeType, CancellationToken ct)
    {
        var workDir = Path.Combine(_opts.TempDirectory, $"audio-{mediaId}");
        Directory.CreateDirectory(workDir);

        try
        {
            var inputPath = Path.Combine(workDir, "input");
            await s3.DownloadToFileAsync(s3Key, inputPath, ct);

            var probe = await ffmpeg.ProbeAsync(inputPath, ct);
            if (probe == null)
                throw new InvalidOperationException("Audio file is corrupt or unsupported.");

            var variants = new List<MediaVariant>();

            // Normalize audio (EBU R128)
            var normalizedPath = Path.Combine(workDir, "normalized.m4a");
            await ffmpeg.NormalizeAudioAsync(inputPath, normalizedPath, ct);

            var normalizedKey = $"media/{mediaId}/audio/normalized.m4a";
            await using (var fs = File.OpenRead(normalizedPath))
            {
                await s3.UploadAsync(normalizedKey, "audio/mp4", fs, ct);
            }

            variants.Add(new MediaVariant
            {
                Kind = "audio-normalized",
                S3Key = normalizedKey,
                MimeType = "audio/mp4",
                Size = new FileInfo(normalizedPath).Length,
                DurationMs = probe.DurationSeconds.HasValue
                    ? (int)(probe.DurationSeconds.Value * 1000)
                    : null,
            });

            logger.LogInformation("Audio processing complete for {MediaId}: normalized", mediaId);
            return variants;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to cleanup {Dir}", workDir); }
        }
    }
}
