using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Domain.ValueObjects;

namespace Haworks.Content.Infrastructure.ExternalServices.Validation;

public class FileSignatureValidator : IFileSignatureValidator
{
    // Largest supported signature is the 8-byte PNG magic number; we only
    // need that many bytes from the head of the stream to make a verdict.
    private const int MaxSignatureBytes = 8;

    private readonly ILogger<FileSignatureValidator> _logger;

    public FileSignatureValidator(ILogger<FileSignatureValidator> logger)
    {
        _logger = logger;
    }

    // Signatures that identify executable/dangerous file types — checked BEFORE
    // the allowlist so that a renamed executable never slips through.
    private static readonly (string Label, byte[] Signature)[] DangerousSignatures =
    [
        ("PE/MZ Windows executable",  new byte[] { 0x4D, 0x5A }),
        ("ELF Linux executable",      new byte[] { 0x7F, 0x45, 0x4C, 0x46 }),
        ("Shell script shebang",      new byte[] { 0x23, 0x21 }),
        ("Java class file",           new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }),
    ];

    private static readonly Dictionary<string, List<byte[]>> FileSignatures = new()
    {
        { "image/jpeg", new List<byte[]> { new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 } } },
        { "image/png",  new List<byte[]> { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } } },
        { "image/gif",  new List<byte[]> { new byte[] { 0x47, 0x49, 0x46, 0x38 } } },
        { "application/pdf", new List<byte[]> { new byte[] { 0x25, 0x50, 0x44, 0x46 } } },
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
            return new FileSignatureValidationResult(false, "Unknown");
        }

        // --- Layer 1: explicit executable/dangerous signature detection ---
        foreach (var (label, sig) in DangerousSignatures)
        {
            if (sig.Length > read) continue;
            if (head.AsSpan(0, sig.Length).SequenceEqual(sig))
            {
                var headHash = Convert.ToHexString(SHA256.HashData(head.AsSpan(0, read)));
                _logger.LogCritical(
                    "SECURITY: Executable file upload blocked. SignatureType={SignatureType} HeadHash={HeadHash}",
                    label, headHash);
                return new FileSignatureValidationResult(false, "application/octet-stream");
            }
        }

        // --- Layer 2: allowlist-based rejection ---
        foreach (var (mime, signatures) in FileSignatures)
        {
            foreach (var sig in signatures)
            {
                if (sig.Length > read) continue;
                if (head.AsSpan(0, sig.Length).SequenceEqual(sig))
                {
                    return new FileSignatureValidationResult(true, mime);
                }
            }
        }

        _logger.LogWarning("File signature validation failed. Unknown file type.");
        return new FileSignatureValidationResult(false, "Unknown");
    }
}
