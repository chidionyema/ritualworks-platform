using Haworks.Contracts.Media;
using Haworks.Media.Api.Options;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;

namespace Haworks.Media.Api.Infrastructure.Processing;

public sealed class ImageProcessor(
    IS3Service s3,
    IOptions<ImageOptions> opts,
    ILogger<ImageProcessor> logger) : IMediaProcessor
{
    private readonly ImageOptions _opts = opts.Value;

    public bool CanProcess(string mimeType) =>
        _opts.Enabled && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !mimeType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase); // SVG skips raster processing

    public async Task<IReadOnlyList<MediaVariant>> ProcessAsync(
        Guid mediaId, string s3Key, string mimeType, CancellationToken ct)
    {
        await using var stream = await s3.DownloadAsync(s3Key, ct);

        // Check dimensions BEFORE loading full image to prevent decompression bombs
        var info = await Image.IdentifyAsync(stream, ct);
        if (info != null && (info.Width > _opts.MaxDimensionPixels || info.Height > _opts.MaxDimensionPixels))
        {
            throw new InvalidOperationException(
                $"Image dimensions ({info.Width}x{info.Height}) exceed max ({_opts.MaxDimensionPixels}px).");
        }

        stream.Position = 0;
        using var image = await Image.LoadAsync(stream, ct);

        // Auto-orient based on EXIF rotation
        image.Mutate(x => x.AutoOrient());

        // Strip GPS data for privacy
        if (_opts.StripExifGps && image.Metadata.ExifProfile != null)
        {
            var gpsTagsToRemove = image.Metadata.ExifProfile.Values
                .Where(v => v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal))
                .Select(v => v.Tag)
                .ToList();
            foreach (var tag in gpsTagsToRemove)
            {
                image.Metadata.ExifProfile.RemoveValue(tag);
            }
        }

        var variants = new List<MediaVariant>();

        foreach (var size in _opts.ThumbnailSizes)
        {
            if (image.Width <= size && image.Height <= size)
                continue; // Skip thumbnails larger than the original

            // JPEG thumbnail
            using var thumb = image.Clone(x => x.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Max, // Fit within, maintain aspect ratio
            }));

            var jpegKey = $"media/{mediaId}/img/thumb-{size}.jpg";
            await using var jpegMs = new MemoryStream();
            await thumb.SaveAsJpegAsync(jpegMs, ct);
            jpegMs.Position = 0;
            await s3.UploadAsync(jpegKey, "image/jpeg", jpegMs, ct);

            variants.Add(new MediaVariant
            {
                Kind = $"thumbnail-{size}",
                S3Key = jpegKey,
                MimeType = "image/jpeg",
                Size = jpegMs.Length,
                Width = thumb.Width,
                Height = thumb.Height,
            });

            // WebP variant
            var webpKey = $"media/{mediaId}/img/thumb-{size}.webp";
            await using var webpMs = new MemoryStream();
            await thumb.SaveAsWebpAsync(webpMs, new WebpEncoder { Quality = _opts.WebPQuality }, ct);
            webpMs.Position = 0;
            await s3.UploadAsync(webpKey, "image/webp", webpMs, ct);

            variants.Add(new MediaVariant
            {
                Kind = $"webp-{size}",
                S3Key = webpKey,
                MimeType = "image/webp",
                Size = webpMs.Length,
                Width = thumb.Width,
                Height = thumb.Height,
            });
        }

        logger.LogInformation("Image processing complete for {MediaId}: {Count} variants", mediaId, variants.Count);
        return variants;
    }
}
