using Microsoft.Extensions.Logging;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Domain.ValueObjects;

namespace Haworks.Content.Infrastructure.ExternalServices.Validation;

public class FileSignatureValidator : IFileSignatureValidator
{
    private readonly ILogger<FileSignatureValidator> _logger;

    public FileSignatureValidator(ILogger<FileSignatureValidator> logger)
    {
        _logger = logger;
    }

    private static readonly Dictionary<string, List<byte[]>> FileSignatures = new()
    {
        { "image/jpeg", new List<byte[]> { new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 } } },
        { "image/png", new List<byte[]> { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } } },
        { "image/gif", new List<byte[]> { new byte[] { 0x47, 0x49, 0x46, 0x38 } } },
        { "application/pdf", new List<byte[]> { new byte[] { 0x25, 0x50, 0x44, 0x46 } } },
    };

    public async Task<FileSignatureValidationResult> ValidateAsync(Stream fileStream)
    {
        if (fileStream == null || fileStream.Length == 0)
        {
            return new FileSignatureValidationResult(false, "Unknown");
        }

        foreach (var signature in FileSignatures)
        {
            fileStream.Position = 0;
            var maxSigLength = signature.Value.Max(x => x.Length);
            var buffer = new byte[maxSigLength];
            
            // Fix CA2022/CA1835 by using ReadExactlyAsync
            await fileStream.ReadExactlyAsync(buffer.AsMemory(0, maxSigLength));

            if (signature.Value.Any(sig => buffer.Take(sig.Length).SequenceEqual(sig)))
            {
                return new FileSignatureValidationResult(true, signature.Key);
            }
        }

        _logger.LogWarning("File signature validation failed. Unknown file type.");
        return new FileSignatureValidationResult(false, "Unknown");
    }
}
