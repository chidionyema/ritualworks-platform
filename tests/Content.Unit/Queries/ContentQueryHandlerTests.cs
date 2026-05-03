using Xunit;
using Haworks.BuildingBlocks.Common;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Application.Queries;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.ValueObjects;
using Haworks.Content.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Haworks.Content.UnitTests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit.Abstractions;

namespace Haworks.Content.UnitTests.Queries;

public class GetContentQueryHandlerTests : TestBase
{
    private readonly Mock<IContentRepository> _contentRepositoryMock;
    private readonly GetContentQueryHandler _handler;

    public GetContentQueryHandlerTests(ITestOutputHelper output) : base(output)
    {
        _contentRepositoryMock = new Mock<IContentRepository>();
        _handler = new GetContentQueryHandler(_contentRepositoryMock.Object, LoggerFactory.CreateLogger<GetContentQueryHandler>());
    }

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handle_WhenContentExists_ReturnsSuccess()
    {
        // Arrange
        var contentId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var content = DomainTestHelpers.CreateContentEntity(
            id: contentId,
            entityId: entityId,
            entityType: "Product",
            contentType: ContentType.Image,
            path: "https://storage.example.com/content.jpg",
            fileSize: 1024);

        _contentRepositoryMock.Setup(r => r.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);

        var query = new GetContentQuery(contentId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(contentId, result.Value.Id);
        Assert.Equal(entityId, result.Value.EntityId);
        Assert.Equal("Product", result.Value.EntityType);
        Assert.Equal("https://storage.example.com/content.jpg", result.Value.Url);
        Assert.Equal("Image", result.Value.ContentType);
        Assert.Equal(1024, result.Value.FileSize);
    }

    [Fact]
    public async Task Handle_WhenContentNotFound_ReturnsFailure()
    {
        // Arrange
        var contentId = Guid.NewGuid();
        _contentRepositoryMock.Setup(r => r.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContentEntity?)null);

        var query = new GetContentQuery(contentId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Content.NotFound", result.Error.Code);
        Assert.Contains(contentId.ToString(), result.Error.Message);
    }

    [Theory]
    [InlineData(ContentType.Image, "Image")]
    [InlineData(ContentType.Video, "Video")]
    [InlineData(ContentType.Document, "Document")]
    [InlineData(ContentType.Other, "Other")]
    public async Task Handle_MapsContentTypeCorrectly(ContentType contentType, string expectedString)
    {
        // Arrange
        var contentId = Guid.NewGuid();
        var content = DomainTestHelpers.CreateContentEntity(
            id: contentId,
            entityType: "Product",
            contentType: contentType,
            path: "https://example.com/file",
            fileSize: 100);

        _contentRepositoryMock.Setup(r => r.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);

        var query = new GetContentQuery(contentId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(expectedString, result.Value.ContentType);
    }
}

public class GetChunkSessionStatusQueryHandlerTests : TestBase
{
    private readonly Mock<IChunkedUploadService> _chunkedServiceMock;
    private readonly GetChunkSessionStatusQueryHandler _handler;

    public GetChunkSessionStatusQueryHandlerTests(ITestOutputHelper output) : base(output)
    {
        _chunkedServiceMock = new Mock<IChunkedUploadService>();
        _handler = new GetChunkSessionStatusQueryHandler(_chunkedServiceMock.Object, LoggerFactory.CreateLogger<GetChunkSessionStatusQueryHandler>());
    }

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handle_WhenSessionExists_ReturnsSession()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var session = new ChunkSession
        {
            Id = sessionId,
            EntityId = entityId,
            FileName = "test-file.mp4",
            TotalChunks = 10,
            TotalSize = 104857600, // 100MB
            ExpiresAt = DateTime.UtcNow.AddHours(6)
        };

        _chunkedServiceMock.Setup(s => s.GetSessionAsync(sessionId))
            .ReturnsAsync(session);

        var query = new GetChunkSessionStatusQuery(sessionId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(sessionId, result.Value.Id);
        Assert.Equal("test-file.mp4", result.Value.FileName);
        Assert.Equal(10, result.Value.TotalChunks);
    }

    [Fact]
    public async Task Handle_WhenSessionNotFound_ReturnsFailure()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _chunkedServiceMock.Setup(s => s.GetSessionAsync(sessionId))
            .ThrowsAsync(new InvalidOperationException("Session not found"));

        var query = new GetChunkSessionStatusQuery(sessionId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Content.SessionNotFound", result.Error.Code);
        Assert.Contains(sessionId.ToString(), result.Error.Message);
    }

    [Fact]
    public async Task Handle_CallsServiceWithCorrectSessionId()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = new ChunkSession
        {
            Id = sessionId,
            EntityId = Guid.NewGuid(),
            FileName = "file.mp4",
            TotalChunks = 5,
            TotalSize = 50000000,
            ExpiresAt = DateTime.UtcNow.AddHours(6)
        };

        _chunkedServiceMock.Setup(s => s.GetSessionAsync(sessionId))
            .ReturnsAsync(session);

        var query = new GetChunkSessionStatusQuery(sessionId);

        // Act
        await _handler.Handle(query, CancellationToken.None);

        // Assert
        _chunkedServiceMock.Verify(s => s.GetSessionAsync(sessionId), Times.Once);
    }
}
