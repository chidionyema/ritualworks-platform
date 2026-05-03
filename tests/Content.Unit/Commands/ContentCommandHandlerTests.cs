using Xunit;
using Haworks.BuildingBlocks.Common;
using Haworks.Content.Application.Commands;
using Haworks.Content.Application.DTOs;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.ValueObjects;
using Haworks.Content.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit.Abstractions;

namespace Haworks.Content.UnitTests.Commands;

public class UploadFileCommandHandlerTests : TestBase
{
    private readonly Mock<IContentStorageService> _storageServiceMock;
    private readonly Mock<IFileValidator> _fileValidatorMock;
    private readonly Mock<IContentRepository> _contentRepositoryMock;
    private readonly UploadFileCommandHandler _handler;

    public UploadFileCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        _storageServiceMock = new Mock<IContentStorageService>();
        _fileValidatorMock = new Mock<IFileValidator>();
        _contentRepositoryMock = new Mock<IContentRepository>();
        _handler = new UploadFileCommandHandler(
            _storageServiceMock.Object,
            _fileValidatorMock.Object,
            _contentRepositoryMock.Object,
            LoggerFactory.CreateLogger<UploadFileCommandHandler>());
    }

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handle_WithNullFile_ReturnsFailure()
    {
        // Arrange
        var command = new UploadFileCommand(Guid.NewGuid(), null!, "user-123");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Content.EmptyFile", result.Error.Code);
    }

    [Fact]
    public async Task Handle_WithEmptyFile_ReturnsFailure()
    {
        // Arrange
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(0);

        var command = new UploadFileCommand(Guid.NewGuid(), fileMock.Object, "user-123");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Content.EmptyFile", result.Error.Code);
    }

    [Fact]
    public async Task Handle_WhenValidationFails_ReturnsFailure()
    {
        // Arrange
        var fileMock = CreateMockFile("test.jpg", 1024, "image/jpeg");
        var validationResult = FileValidationResult.Failure("Invalid file type");

        _fileValidatorMock.Setup(v => v.ValidateAsync(fileMock.Object))
            .ReturnsAsync(validationResult);

        var command = new UploadFileCommand(Guid.NewGuid(), fileMock.Object, "user-123");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Content.ValidationFailed", result.Error.Code);
        Assert.Contains("Invalid file type", result.Error.Message);
    }

    [Fact]
    public async Task Handle_WhenStorageUploadFails_ReturnsFailure()
    {
        // Arrange
        var fileMock = CreateMockFile("test.jpg", 1024, "image/jpeg");
        var validationResult = FileValidationResult.Success("image");

        _fileValidatorMock.Setup(v => v.ValidateAsync(fileMock.Object))
            .ReturnsAsync(validationResult);
        _storageServiceMock.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Storage error"));

        var command = new UploadFileCommand(Guid.NewGuid(), fileMock.Object, "user-123");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Content.StorageFailed", result.Error.Code);
    }

    [Fact]
    public async Task Handle_WhenDatabaseSaveFails_ReturnsFailureAndCleansUpStorage()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var fileMock = CreateMockFile("test.jpg", 1024, "image/jpeg");
        var validationResult = FileValidationResult.Success("image");
        var uploadResult = new ContentUploadResult(
            BucketName: "images",
            ObjectName: "user-123/guid.jpg",
            ContentType: "image/jpeg",
            FileSize: 1024,
            VersionId: "v1",
            StorageDetails: "stored",
            Path: "https://storage.example.com/images/user-123/guid.jpg");

        _fileValidatorMock.Setup(v => v.ValidateAsync(fileMock.Object))
            .ReturnsAsync(validationResult);
        _storageServiceMock.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadResult);
        _contentRepositoryMock.Setup(r => r.AddContentsAsync(It.IsAny<IEnumerable<ContentEntity>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        var command = new UploadFileCommand(entityId, fileMock.Object, "user-123");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Content.DatabaseFailed", result.Error.Code);

        // Verify storage cleanup was called
        _storageServiceMock.Verify(s => s.DeleteAsync(
            uploadResult.BucketName,
            uploadResult.ObjectName,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithValidImageFile_ReturnsSuccess()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var fileMock = CreateMockFile("test.jpg", 1024, "image/jpeg");
        var validationResult = FileValidationResult.Success("image");
        var uploadResult = new ContentUploadResult(
            BucketName: "images",
            ObjectName: "user-123/guid.jpg",
            ContentType: "image/jpeg",
            FileSize: 1024,
            VersionId: "v1",
            StorageDetails: "stored",
            Path: "https://storage.example.com/images/user-123/guid.jpg");

        _fileValidatorMock.Setup(v => v.ValidateAsync(fileMock.Object))
            .ReturnsAsync(validationResult);
        _storageServiceMock.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(),
                "images",
                It.IsAny<string>(),
                "image/jpeg",
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadResult);
        _contentRepositoryMock.Setup(r => r.AddContentsAsync(It.IsAny<IEnumerable<ContentEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new UploadFileCommand(entityId, fileMock.Object, "user-123");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(entityId, result.Value.EntityId);
        Assert.Equal("images", result.Value.EntityType);
        Assert.Equal("Image", result.Value.ContentType);
    }

    [Fact]
    public async Task Handle_WithVideoFile_UsesVideosBucket()
    {
        // Arrange
        var fileMock = CreateMockFile("video.mp4", 10000, "video/mp4");
        var validationResult = FileValidationResult.Success("video");
        var uploadResult = new ContentUploadResult(
            BucketName: "videos",
            ObjectName: "user-123/guid.mp4",
            ContentType: "video/mp4",
            FileSize: 10000,
            VersionId: "v1",
            StorageDetails: "stored",
            Path: "https://storage.example.com/videos/user-123/guid.mp4");

        _fileValidatorMock.Setup(v => v.ValidateAsync(fileMock.Object))
            .ReturnsAsync(validationResult);
        _storageServiceMock.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(),
                "videos",
                It.IsAny<string>(),
                "video/mp4",
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadResult);
        _contentRepositoryMock.Setup(r => r.AddContentsAsync(It.IsAny<IEnumerable<ContentEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new UploadFileCommand(Guid.NewGuid(), fileMock.Object, "user-123");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Video", result.Value.ContentType);
        _storageServiceMock.Verify(s => s.UploadAsync(
            It.IsAny<Stream>(),
            "videos",
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithDocumentFile_UsesDocumentsBucket()
    {
        // Arrange
        var fileMock = CreateMockFile("document.pdf", 5000, "application/pdf");
        var validationResult = FileValidationResult.Success("document");
        var uploadResult = new ContentUploadResult(
            BucketName: "documents",
            ObjectName: "user-123/guid.pdf",
            ContentType: "application/pdf",
            FileSize: 5000,
            VersionId: "v1",
            StorageDetails: "stored",
            Path: "https://storage.example.com/documents/user-123/guid.pdf");

        _fileValidatorMock.Setup(v => v.ValidateAsync(fileMock.Object))
            .ReturnsAsync(validationResult);
        _storageServiceMock.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(),
                "documents",
                It.IsAny<string>(),
                "application/pdf",
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadResult);
        _contentRepositoryMock.Setup(r => r.AddContentsAsync(It.IsAny<IEnumerable<ContentEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new UploadFileCommand(Guid.NewGuid(), fileMock.Object, "user-123");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Document", result.Value.ContentType);
    }

    [Fact]
    public async Task Handle_WithUnknownFileType_UsesOtherUploadsBucket()
    {
        // Arrange
        var fileMock = CreateMockFile("data.bin", 1000, "application/octet-stream");
        var validationResult = FileValidationResult.Success("unknown");
        var uploadResult = new ContentUploadResult(
            BucketName: "other-uploads",
            ObjectName: "user-123/guid.bin",
            ContentType: "application/octet-stream",
            FileSize: 1000,
            VersionId: "v1",
            StorageDetails: "stored",
            Path: "https://storage.example.com/other-uploads/user-123/guid.bin");

        _fileValidatorMock.Setup(v => v.ValidateAsync(fileMock.Object))
            .ReturnsAsync(validationResult);
        _storageServiceMock.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(),
                "other-uploads",
                It.IsAny<string>(),
                "application/octet-stream",
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadResult);
        _contentRepositoryMock.Setup(r => r.AddContentsAsync(It.IsAny<IEnumerable<ContentEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new UploadFileCommand(Guid.NewGuid(), fileMock.Object, "user-123");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Other", result.Value.ContentType);
    }

    private static Mock<IFormFile> CreateMockFile(string fileName, long length, string contentType)
    {
        var content = new byte[length];
        var stream = new MemoryStream(content);

        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns(fileName);
        fileMock.Setup(f => f.Length).Returns(length);
        fileMock.Setup(f => f.ContentType).Returns(contentType);
        fileMock.Setup(f => f.OpenReadStream()).Returns(stream);

        return fileMock;
    }
}
