using FluentValidation;
using Haworks.Content.Application.Commands;

namespace Haworks.Content.Application.Validators;

/// <summary>
/// Validator for upload file command.
/// </summary>
internal sealed class UploadFileCommandValidator : AbstractValidator<UploadFileCommand>
{
    /// <summary>
    /// Maximum file size in bytes (50 MB).
    /// </summary>
    private const long MaxFileSize = 50 * 1024 * 1024;

    /// <summary>
    /// Allowed content types for uploads.
    /// </summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
        "application/pdf",
        "video/mp4",
        "video/webm"
    };

    public UploadFileCommandValidator()
    {
        RuleFor(x => x.EntityId)
            .NotEmpty()
            .WithMessage("Entity ID is required");

        RuleFor(x => x.UserId)
            .NotEmpty()
            .WithMessage("User ID is required");

        RuleFor(x => x.File)
            .NotNull()
            .WithMessage("File is required");

        When(x => x.File != null, () =>
        {
            RuleFor(x => x.File.Length)
                .GreaterThan(0)
                .WithMessage("File is empty")
                .LessThanOrEqualTo(MaxFileSize)
                .WithMessage($"File size cannot exceed {MaxFileSize / (1024 * 1024)} MB");

            RuleFor(x => x.File.ContentType)
                .NotEmpty()
                .WithMessage("Content type is required")
                .Must(ct => AllowedContentTypes.Contains(ct))
                .WithMessage("File type is not allowed. Allowed types: JPEG, PNG, GIF, WebP, PDF, MP4, WebM");

            RuleFor(x => x.File.FileName)
                .NotEmpty()
                .WithMessage("File name is required")
                .MaximumLength(255)
                .WithMessage("File name cannot exceed 255 characters")
                .Must(name => !name.Contains("..") && !Path.GetInvalidFileNameChars().Any(name.Contains))
                .WithMessage("Invalid file name");
        });
    }
}
