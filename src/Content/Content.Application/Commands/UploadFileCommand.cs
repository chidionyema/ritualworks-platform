using Microsoft.AspNetCore.Http;
using Haworks.Content.Application.DTOs;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Content.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using Microsoft.Extensions.Logging;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.Interfaces;
using MediatR;

namespace Haworks.Content.Application.Commands;

public sealed record UploadFileCommand(
    Guid EntityId,
    IFormFile File,
    string UserId
) : IRequest<Result<ContentDto>>;

internal sealed class UploadFileCommandHandler : IRequestHandler<UploadFileCommand, Result<ContentDto>>
{
    private readonly IContentStorageService _storageService;
    private readonly IFileValidator _fileValidator;
    private readonly IContentRepository _contentRepository;
    private readonly ILogger<UploadFileCommandHandler> _logger;

    public UploadFileCommandHandler(
        IContentStorageService storageService,
        IFileValidator fileValidator,
        IContentRepository contentRepository,
        ILogger<UploadFileCommandHandler> logger)
    {
        _storageService = storageService;
        _fileValidator = fileValidator;
        _contentRepository = contentRepository;
        _logger = logger;
    }

    public async Task<Result<ContentDto>> Handle(UploadFileCommand request, CancellationToken cancellationToken)
    {
        if (request.File == null || request.File.Length == 0)
        {
            return Result.Failure<ContentDto>(Error.Content.EmptyFile);
        }

        _logger.LogInformation(
            "Starting file upload. EntityId: {EntityId}, FileName: {FileName}, Size: {FileSize}",
            request.EntityId, request.File.FileName, request.File.Length);

        var validationResult = await _fileValidator.ValidateAsync(request.File);
        if (!validationResult.IsValid)
        {
            return Result.Failure<ContentDto>(
                new Error("Content.ValidationFailed", string.Join(", ", validationResult.Errors), ErrorType.Validation));
        }

        ContentUploadResult uploadResult;
        try
        {
            await using var fileStream = request.File.OpenReadStream();
            uploadResult = await _storageService.UploadAsync(
                fileStream,
                GetBucketForType(validationResult.FileType),
                GenerateObjectName(request.File.FileName, request.UserId),
                request.File.ContentType,
                new Dictionary<string, string>
                {
                    ["FileType"] = validationResult.FileType ?? "unknown",
                    ["UploadedBy"] = request.UserId
                },
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Storage upload failed for {FileName}", request.File.FileName);
            return Result.Failure<ContentDto>(Error.Storage("Content.StorageFailed", "Failed to upload file to storage."));
        }

        var content = ContentEntity.Create(
            request.EntityId,
            GetBucketForType(validationResult.FileType),
            ParseContentType(request.File.ContentType));

        content.SetStorageInfo(
            uploadResult.BucketName,
            uploadResult.ObjectName,
            uploadResult.ObjectName, // blobName same as objectName
            request.File.Length);
        content.SetFileInfo(request.File.FileName, uploadResult.VersionId, string.Empty);
        content.SetUrlInfo(string.Empty, uploadResult.Path);
        content.SetStorageDetails(uploadResult.StorageDetails);

        try
        {
            await _contentRepository.AddContentsAsync(new[] { content }, cancellationToken);
            _logger.LogInformation("Content record added. ContentId: {ContentId}", content.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database insertion failed for file {FileName}", request.File.FileName);

            // Cleanup uploaded file on DB failure
            try
            {
                await _storageService.DeleteAsync(uploadResult.BucketName, uploadResult.ObjectName, cancellationToken);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Failed to cleanup uploaded file after DB failure");
            }

            return Result.Failure<ContentDto>(Error.Database("Content.DatabaseFailed", "Failed to save content metadata."));
        }

        return Result.Success(MapToDto(content));
    }

    private static string GetBucketForType(string? fileType) =>
        fileType?.ToLowerInvariant() switch
        {
            "image" => "images",
            "document" => "documents",
            "video" => "videos",
            _ => "other-uploads"
        };

    private static string GenerateObjectName(string fileName, string userId)
    {
        var sanitizedFileName = Path.GetFileName(fileName);
        return $"{userId}/{Guid.NewGuid()}{Path.GetExtension(sanitizedFileName)}";
    }

    private static ContentDto MapToDto(ContentEntity content) =>
        new(content.Id, content.EntityId, content.EntityType, content.Path,
            content.ContentType.ToString(), content.FileSize);

    private static ContentType ParseContentType(string mime) =>
        mime.ToLowerInvariant() switch
        {
            var m when m.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => ContentType.Image,
            "application/pdf" => ContentType.Document,
            var m when m.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => ContentType.Video,
            _ => ContentType.Other
        };
}
