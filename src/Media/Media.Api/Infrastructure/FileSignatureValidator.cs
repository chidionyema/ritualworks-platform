using Microsoft.Extensions.Logging;

namespace Haworks.Media.Api.Infrastructure;

public record FileSignatureValidationResult(bool IsValid, string FileType);

public interface IFileSignatureValidator
{
    Task<FileSignatureValidationResult> ValidateAsync(Stream fileStream, CancellationToken ct = default);
}

public sealed class FileSignatureValidator : IFileSignatureValidator
{
    private const int MaxSignatureBytes = 12;
    private readonly ILogger<FileSignatureValidator> _logger;

    public FileSignatureValidator(ILogger<FileSignatureValidator> logger)
    {
        _logger = logger;
    }

    private static readonly Dictionary<string, List<byte[]>> FileSignatures = new()
    {
        { "image/jpeg", [
            [0xFF, 0xD8, 0xFF, 0xE0],
            [0xFF, 0xD8, 0xFF, 0xE1],
            [0xFF, 0xD8, 0xFF, 0xE2],
            [0xFF, 0xD8, 0xFF, 0xE3],
            [0xFF, 0xD8, 0xFF, 0xE8],
        ]},
        { "image/png", [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]] },
        { "image/gif", [[0x47, 0x49, 0x46, 0x38, 0x37, 0x61], [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]] },
        { "application/pdf", [[0x25, 0x50, 0x44, 0x46, 0x2D]] },
        { "image/webp", [[0x52, 0x49, 0x46, 0x46]] },
        { "video/mp4", [[0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70], [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70]] },
        { "audio/mpeg", [[0x49, 0x44, 0x33], [0xFF, 0xFB], [0xFF, 0xF3], [0xFF, 0xF2]] },
        { "audio/ogg", [[0x4F, 0x67, 0x67, 0x53]] },
        { "audio/flac", [[0x66, 0x4C, 0x61, 0x43]] },
        { "video/webm", [[0x1A, 0x45, 0xDF, 0xA3]] },
        { "video/x-matroska", [[0x1A, 0x45, 0xDF, 0xA3]] },
    };

    public async Task<FileSignatureValidationResult> ValidateAsync(Stream fileStream, CancellationToken ct = default)
    {
        if (fileStream is null)
            return new FileSignatureValidationResult(false, "Unknown");

        var head = new byte[MaxSignatureBytes];
        var read = 0;
        while (read < MaxSignatureBytes)
        {
            var n = await fileStream.ReadAsync(head.AsMemory(read, MaxSignatureBytes - read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }

        if (read == 0)
        {
            _logger.LogWarning("File signature validation failed — empty stream");
            return new FileSignatureValidationResult(false, "Unknown");
        }

        foreach (var (mime, signatures) in FileSignatures)
        {
            foreach (var sig in signatures)
            {
                if (sig.Length > read) continue;
                if (!head.AsSpan(0, sig.Length).SequenceEqual(sig)) continue;

                if (string.Equals(mime, "image/webp", StringComparison.Ordinal))
                {
                    if (read >= 12 && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
                        return new FileSignatureValidationResult(true, mime);
                    continue;
                }

                return new FileSignatureValidationResult(true, mime);
            }
        }

        if (read >= 2 && head[0] == 0x4D && head[1] == 0x5A)
            _logger.LogCritical("Security: attempted upload of executable file (MZ header) blocked");
        else
            _logger.LogWarning("File signature validation failed — unknown or untrusted file type");

        return new FileSignatureValidationResult(false, "Unknown");
    }
}
