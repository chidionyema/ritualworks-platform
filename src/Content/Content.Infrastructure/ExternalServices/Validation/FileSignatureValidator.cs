using Microsoft.Extensions.Logging;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Domain.ValueObjects;

namespace Haworks.Content.Infrastructure.ExternalServices.Validation;

public class FileSignatureValidator : IFileSignatureValidator
{
    // Largest supported signature is the 8-byte PNG magic number or 12-byte WebP.
    // We'll use 12 bytes to be safe for most common types.
    private const int MaxSignatureBytes = 12;

    private readonly ILogger<FileSignatureValidator> _logger;

    public FileSignatureValidator(ILogger<FileSignatureValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Strict allowlist of permitted file types. 
    /// Executable types (e.g., .exe, .dll, .sh, .bat) are EXPLICITLY excluded.
    /// </summary>
    private static readonly Dictionary<string, List<byte[]>> FileSignatures = new()
    {
        { "image/jpeg", new List<byte[]> 
            { 
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, 
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 },
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE2 },
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE3 },
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE8 }
            } 
        },
        { "image/png",  new List<byte[]> { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } } },
        { "image/gif",  new List<byte[]> { new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 } } },
        { "application/pdf", new List<byte[]> { new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D } } },
        { "image/webp", new List<byte[]> { new byte[] { 0x52, 0x49, 0x46, 0x46 } } }, // RIFF header (first 4 bytes)
    };

    public async Task<FileSignatureValidationResult> ValidateAsync(Stream fileStream)
    {
        if (fileStream is null)
        {
            return new FileSignatureValidationResult(false, "Unknown");
        }

        // Read up to MaxSignatureBytes from the head of the stream WITHOUT
        // touching Position or Length — both throw NotSupportedException on
        // non-seekable streams (e.g. AWS S3's GetObjectResponse.ResponseStream).
        var head = new byte[MaxSignatureBytes];
        var read = 0;
        while (read < MaxSignatureBytes)
        {
            var n = await fileStream.ReadAsync(head.AsMemory(read, MaxSignatureBytes - read))
                .ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }

        if (read == 0)
        {
            _logger.LogWarning("File signature validation failed. Empty stream.");
            return new FileSignatureValidationResult(false, "Unknown");
        }

        // Detect known safe types from the allowlist
        foreach (var (mime, signatures) in FileSignatures)
        {
            foreach (var sig in signatures)
            {
                if (sig.Length > read) continue;
                if (head.AsSpan(0, sig.Length).SequenceEqual(sig))
                {
                    // For WebP, we also need to check the 8-11 bytes for "WEBP"
                    if (string.Equals(mime, "image/webp", StringComparison.Ordinal))
                    {
                        if (read >= 12 && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
                        {
                            return new FileSignatureValidationResult(true, mime);
                        }
                        continue; // Not a WebP
                    }

                    return new FileSignatureValidationResult(true, mime);
                }
            }
        }

        // Check for common malicious/executable headers to log them specifically
        if (read >= 2 && head[0] == 0x4D && head[1] == 0x5A)
        {
            _logger.LogCritical("Security Alert: Attempted upload of executable file (MZ header). Content blocked.");
        }
        else
        {
            _logger.LogWarning("File signature validation failed. Unknown or untrusted file type.");
        }

        return new FileSignatureValidationResult(false, "Unknown");
    }
}
