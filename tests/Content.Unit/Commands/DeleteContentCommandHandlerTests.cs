using Xunit;
using Haworks.BuildingBlocks.Common;
using Haworks.Content.Application.Commands;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Haworks.Content.UnitTests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit.Abstractions;

namespace Haworks.Content.UnitTests.Commands.Content;

public class DeleteContentCommandHandlerTests : TestBase
{
    private readonly Mock<IContentStorageService> _storageServiceMock;
    private readonly Mock<IContentRepository> _contentRepositoryMock;
    private readonly DeleteContentCommandHandler _handler;

    public DeleteContentCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        _storageServiceMock = new Mock<IContentStorageService>();
        _contentRepositoryMock = new Mock<IContentRepository>();

        _handler = new DeleteContentCommandHandler(
            _storageServiceMock.Object,
            _contentRepositoryMock.Object,
            LoggerFactory.CreateLogger<DeleteContentCommandHandler>());
    }

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    #region Validation Tests

    [Fact]
    public async Task Handle_WithNonExistentContent_ReturnsNotFoundError()
    {
        // Arrange
        var contentId = Guid.NewGuid();

        _contentRepositoryMock
            .Setup(x => x.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContentEntity?)null);

        var command = new DeleteContentCommand(contentId);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Content.NotFound", result.Error.Code);
    }

    #endregion

    #region Success Tests

    [Fact]
    public async Task Handle_WithExistingContent_DeletesFromStorageAndDatabase()
    {
        // Arrange
        var contentId = Guid.NewGuid();
        var existingContent = DomainTestHelpers.CreateContentEntity(
            id: contentId,
            entityType: "images",
            contentType: ContentType.Image,
            bucketName: "images",
            objectName: "user123/abc123.jpg",
            fileName: "test-image.jpg",
            fileSize: 1024);

        _contentRepositoryMock
            .Setup(x => x.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingContent);

        _storageServiceMock
            .Setup(x => x.DeleteAsync("images", "user123/abc123.jpg", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _contentRepositoryMock
            .Setup(x => x.RemoveContentAsync(existingContent, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new DeleteContentCommand(contentId);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Handle_WhenStorageDeleteFails_StillDeletesFromDatabase()
    {
        // Arrange
        var contentId = Guid.NewGuid();
        var existingContent = DomainTestHelpers.CreateContentEntity(
            id: contentId,
            entityType: "documents",
            contentType: ContentType.Document,
            bucketName: "documents",
            objectName: "user456/doc789.pdf",
            fileName: "test.pdf",
            fileSize: 2048);

        _contentRepositoryMock
            .Setup(x => x.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingContent);

        _storageServiceMock
            .Setup(x => x.DeleteAsync("documents", "user456/doc789.pdf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Storage service unavailable"));

        _contentRepositoryMock
            .Setup(x => x.RemoveContentAsync(existingContent, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new DeleteContentCommand(contentId);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert - Should still succeed even if storage delete fails
        Assert.True(result.IsSuccess);
    }

    #endregion

    #region Repository Interaction Tests

    [Fact]
    public async Task Handle_CallsDeleteAsyncOnStorage()
    {
        // Arrange
        var contentId = Guid.NewGuid();
        var existingContent = DomainTestHelpers.CreateContentEntity(
            id: contentId,
            entityType: "videos",
            contentType: ContentType.Video,
            bucketName: "videos",
            objectName: "user789/video123.mp4",
            fileName: "video.mp4",
            fileSize: 10240);

        _contentRepositoryMock
            .Setup(x => x.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingContent);

        _storageServiceMock
            .Setup(x => x.DeleteAsync("videos", "user789/video123.mp4", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _contentRepositoryMock
            .Setup(x => x.RemoveContentAsync(existingContent, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new DeleteContentCommand(contentId);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _storageServiceMock.Verify(
            x => x.DeleteAsync("videos", "user789/video123.mp4", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_CallsRemoveContentAsync()
    {
        // Arrange
        var contentId = Guid.NewGuid();
        var existingContent = DomainTestHelpers.CreateContentEntity(
            id: contentId,
            entityType: "images",
            contentType: ContentType.Image,
            bucketName: "images",
            objectName: "user/photo.png",
            fileName: "photo.png",
            fileSize: 512);

        _contentRepositoryMock
            .Setup(x => x.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingContent);

        _storageServiceMock
            .Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _contentRepositoryMock
            .Setup(x => x.RemoveContentAsync(existingContent, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new DeleteContentCommand(contentId);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _contentRepositoryMock.Verify(
            x => x.RemoveContentAsync(existingContent, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_DoesNotCallStorageWhenContentNotFound()
    {
        // Arrange
        var contentId = Guid.NewGuid();

        _contentRepositoryMock
            .Setup(x => x.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContentEntity?)null);

        var command = new DeleteContentCommand(contentId);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _storageServiceMock.Verify(
            x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _contentRepositoryMock.Verify(
            x => x.RemoveContentAsync(It.IsAny<ContentEntity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region Order of Operations Tests

    [Fact]
    public async Task Handle_DeletesFromStorageBeforeDatabase()
    {
        // Arrange
        var contentId = Guid.NewGuid();
        var callOrder = new System.Collections.Generic.List<string>();
        var existingContent = DomainTestHelpers.CreateContentEntity(
            id: contentId,
            entityType: "images",
            contentType: ContentType.Image,
            bucketName: "images",
            objectName: "test.jpg",
            fileName: "test.jpg",
            fileSize: 100);

        _contentRepositoryMock
            .Setup(x => x.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingContent);

        _storageServiceMock
            .Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("storage"))
            .Returns(Task.CompletedTask);

        _contentRepositoryMock
            .Setup(x => x.RemoveContentAsync(It.IsAny<ContentEntity>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("database"))
            .Returns(Task.CompletedTask);

        var command = new DeleteContentCommand(contentId);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert - Storage should be called before database
        Assert.Equal(2, callOrder.Count);
        Assert.Equal("storage", callOrder[0]);
        Assert.Equal("database", callOrder[1]);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task Handle_WithEmptyGuid_ReturnsNotFound()
    {
        // Arrange
        var emptyGuid = Guid.Empty;

        _contentRepositoryMock
            .Setup(x => x.GetContentByIdAsync(emptyGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContentEntity?)null);

        var command = new DeleteContentCommand(emptyGuid);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Content.NotFound", result.Error.Code);
    }

    #endregion
}
