using Xunit;
using Haworks.BuildingBlocks.Common;
using FluentValidation.TestHelper;
using Haworks.Content.Api.Models;
using Haworks.Content.Api.Validators;

namespace Haworks.Content.UnitTests.Validators;

public class InitChunkSessionRequestValidatorTests
{
    private readonly InitChunkSessionRequestValidator _validator = new();

    private static InitChunkSessionRequest CreateValidRequest() => new(
        EntityId: Guid.NewGuid(),
        FileName: "video.mp4",
        ContentType: "video/mp4",
        TotalChunks: 10,
        TotalSize: 52428800, // 50MB
        ChunkSize: 5242880   // 5MB
    );

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        var request = CreateValidRequest();
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    #region EntityId Validation

    [Fact]
    public void Validate_WithEmptyEntityId_ShouldHaveError()
    {
        var request = CreateValidRequest() with { EntityId = Guid.Empty };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.EntityId)
            .WithErrorMessage("Entity ID is required.");
    }

    #endregion

    #region FileName Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyFileName_ShouldHaveError(string? fileName)
    {
        var request = CreateValidRequest() with { FileName = fileName! };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.FileName);
    }

    [Fact]
    public void Validate_WithFileNameTooLong_ShouldHaveError()
    {
        var request = CreateValidRequest() with { FileName = new string('a', 256) + ".mp4" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.FileName)
            .WithErrorMessage("File name cannot exceed 255 characters.");
    }

    [Theory]
    [InlineData("file/name.mp4")]    // Contains / - invalid on all platforms
    public void Validate_WithInvalidFileNameChars_ShouldHaveError(string fileName)
    {
        var request = CreateValidRequest() with { FileName = fileName };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.FileName)
            .WithErrorMessage("File name contains invalid characters.");
    }

    [Fact]
    public void Validate_WithNullCharInFileName_ShouldHaveError()
    {
        // Null character is invalid on all platforms
        var request = CreateValidRequest() with { FileName = "file\0name.mp4" };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.FileName)
            .WithErrorMessage("File name contains invalid characters.");
    }

    // Platform-specific: These characters are invalid on Windows but valid on macOS/Linux
    [Theory]
    [InlineData("file\\name.mp4")]   // Contains \ - Windows only
    [InlineData("file:name.mp4")]    // Contains : - Windows only
    [InlineData("file*name.mp4")]    // Contains * - Windows only
    [InlineData("file?name.mp4")]    // Contains ? - Windows only
    [InlineData("file<name.mp4")]    // Contains < - Windows only
    [InlineData("file>name.mp4")]    // Contains > - Windows only
    [InlineData("file|name.mp4")]    // Contains | - Windows only
    public void Validate_WithWindowsInvalidFileNameChars_MayHaveError(string fileName)
    {
        var request = CreateValidRequest() with { FileName = fileName };
        var result = _validator.TestValidate(request);
        // On Windows these should fail, on macOS/Linux they should pass
        // This test documents the platform-specific behavior
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var hasInvalidChar = fileName.Any(c => invalidChars.Contains(c));

        if (hasInvalidChar)
        {
            result.ShouldHaveValidationErrorFor(x => x.FileName);
        }
        else
        {
            result.ShouldNotHaveValidationErrorFor(x => x.FileName);
        }
    }

    [Theory]
    [InlineData("valid_file-name.mp4")]
    [InlineData("file.with.dots.mp4")]
    [InlineData("file name with spaces.mp4")]
    [InlineData("UPPERCASE.MP4")]
    public void Validate_WithValidFileName_ShouldNotHaveError(string fileName)
    {
        var request = CreateValidRequest() with { FileName = fileName };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.FileName);
    }

    #endregion

    #region ContentType Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyContentType_ShouldHaveError(string? contentType)
    {
        var request = CreateValidRequest() with { ContentType = contentType! };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ContentType);
    }

    [Theory]
    [InlineData("application/exe")]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    [InlineData("image/svg+xml")]
    public void Validate_WithDisallowedContentType_ShouldHaveError(string contentType)
    {
        var request = CreateValidRequest() with { ContentType = contentType };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ContentType)
            .WithErrorMessage("Content type is not allowed.");
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("video/mp4")]
    [InlineData("video/webm")]
    [InlineData("video/quicktime")]
    [InlineData("audio/mpeg")]
    [InlineData("audio/wav")]
    [InlineData("audio/ogg")]
    [InlineData("application/pdf")]
    [InlineData("application/zip")]
    [InlineData("model/gltf-binary")]
    [InlineData("model/gltf+json")]
    public void Validate_WithAllowedContentType_ShouldNotHaveError(string contentType)
    {
        var request = CreateValidRequest() with { ContentType = contentType };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.ContentType);
    }

    [Theory]
    [InlineData("IMAGE/JPEG")]
    [InlineData("Video/MP4")]
    [InlineData("APPLICATION/PDF")]
    public void Validate_WithContentTypeCaseInsensitive_ShouldNotHaveError(string contentType)
    {
        var request = CreateValidRequest() with { ContentType = contentType };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.ContentType);
    }

    #endregion

    #region TotalChunks Validation

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithTotalChunksNotGreaterThanZero_ShouldHaveError(int totalChunks)
    {
        var request = CreateValidRequest() with { TotalChunks = totalChunks };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.TotalChunks)
            .WithErrorMessage("Total chunks must be greater than 0.");
    }

    [Fact]
    public void Validate_WithTotalChunksExceedsMax_ShouldHaveError()
    {
        var request = CreateValidRequest() with { TotalChunks = 10001 };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.TotalChunks)
            .WithErrorMessage("Total chunks cannot exceed 10,000.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10000)]
    public void Validate_WithValidTotalChunks_ShouldNotHaveError(int totalChunks)
    {
        var request = CreateValidRequest() with { TotalChunks = totalChunks };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.TotalChunks);
    }

    #endregion

    #region TotalSize Validation

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithTotalSizeNotGreaterThanZero_ShouldHaveError(long totalSize)
    {
        var request = CreateValidRequest() with { TotalSize = totalSize };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.TotalSize)
            .WithErrorMessage("Total size must be greater than 0.");
    }

    [Fact]
    public void Validate_WithTotalSizeExceeds10GB_ShouldHaveError()
    {
        var tenGBPlusOne = (10L * 1024 * 1024 * 1024) + 1;
        var request = CreateValidRequest() with { TotalSize = tenGBPlusOne };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.TotalSize)
            .WithErrorMessage("Total size cannot exceed 10GB.");
    }

    [Fact]
    public void Validate_WithTotalSizeExactly10GB_ShouldNotHaveError()
    {
        var tenGB = 10L * 1024 * 1024 * 1024;
        var request = CreateValidRequest() with { TotalSize = tenGB };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.TotalSize);
    }

    #endregion

    #region ChunkSize Validation

    [Fact]
    public void Validate_WithChunkSizeBelowMin_ShouldHaveError()
    {
        var belowOneMB = (1024 * 1024) - 1;
        var request = CreateValidRequest() with { ChunkSize = belowOneMB };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ChunkSize)
            .WithErrorMessage("Chunk size must be at least 1MB.");
    }

    [Fact]
    public void Validate_WithChunkSizeExactlyMin_ShouldNotHaveError()
    {
        var oneMB = 1024 * 1024;
        var request = CreateValidRequest() with { ChunkSize = oneMB };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.ChunkSize);
    }

    [Fact]
    public void Validate_WithChunkSizeExceedsMax_ShouldHaveError()
    {
        var aboveHundredMB = (100 * 1024 * 1024) + 1;
        var request = CreateValidRequest() with { ChunkSize = aboveHundredMB };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ChunkSize)
            .WithErrorMessage("Chunk size cannot exceed 100MB.");
    }

    [Fact]
    public void Validate_WithChunkSizeExactlyMax_ShouldNotHaveError()
    {
        var hundredMB = 100 * 1024 * 1024;
        var request = CreateValidRequest() with { ChunkSize = hundredMB };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.ChunkSize);
    }

    #endregion
}
