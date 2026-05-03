using FluentValidation;
using Haworks.Content.Api.Models;

namespace Haworks.Content.Api.Validators;

public class InitChunkSessionRequestValidator : AbstractValidator<InitChunkSessionRequest>
{
    private static readonly string[] AllowedContentTypes = new[]
    {
        "image/jpeg", "image/png", "image/gif", "image/webp",
        "video/mp4", "video/webm", "video/quicktime",
        "audio/mpeg", "audio/wav", "audio/ogg",
        "application/pdf", "application/zip",
        "model/gltf-binary", "model/gltf+json"
    };

    public InitChunkSessionRequestValidator()
    {
        RuleFor(x => x.EntityId)
            .NotEmpty().WithMessage("Entity ID is required.");

        RuleFor(x => x.FileName)
            .NotEmpty().WithMessage("File name is required.")
            .MaximumLength(255).WithMessage("File name cannot exceed 255 characters.")
            .Must(BeValidFileName).WithMessage("File name contains invalid characters.");

        RuleFor(x => x.ContentType)
            .NotEmpty().WithMessage("Content type is required.")
            .Must(BeAllowedContentType).WithMessage("Content type is not allowed.");

        RuleFor(x => x.TotalChunks)
            .GreaterThan(0).WithMessage("Total chunks must be greater than 0.")
            .LessThanOrEqualTo(10000).WithMessage("Total chunks cannot exceed 10,000.");

        RuleFor(x => x.TotalSize)
            .GreaterThan(0).WithMessage("Total size must be greater than 0.")
            .LessThanOrEqualTo(10L * 1024 * 1024 * 1024).WithMessage("Total size cannot exceed 10GB.");

        RuleFor(x => x.ChunkSize)
            .GreaterThanOrEqualTo(1024 * 1024).WithMessage("Chunk size must be at least 1MB.")
            .LessThanOrEqualTo(100 * 1024 * 1024).WithMessage("Chunk size cannot exceed 100MB.");
    }

    private static bool BeValidFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var invalidChars = Path.GetInvalidFileNameChars();
        return !fileName.Any(c => invalidChars.Contains(c));
    }

    private static bool BeAllowedContentType(string contentType)
    {
        return AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);
    }
}
