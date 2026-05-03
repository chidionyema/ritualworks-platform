using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Haworks.BuildingBlocks.Common;
using System.Security.Claims;
using Haworks.Content.Api.Controllers;
using Haworks.Content.Api.Models;
using Haworks.Content.Application.Commands;
using Haworks.Content.Application.DTOs;
using Haworks.Content.Application.Queries;
using Haworks.BuildingBlocks.Common;
using Haworks.Content.Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Haworks.Content.UnitTests.Controllers
{
    public class ContentControllerTests
    {
        private readonly Mock<IMediator> _mediatorMock;
        private readonly ContentController _controller;
        private readonly string _testUserId = "test-user-123";

        public ContentControllerTests()
        {
            _mediatorMock = new Mock<IMediator>();
            _controller = new ContentController(_mediatorMock.Object);

            // Setup default authenticated HTTP context
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, _testUserId)
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var principal = new ClaimsPrincipal(identity);

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = principal
                }
            };
        }

        #region Constructor Tests

        [Fact]
        public void Constructor_WithNullMediator_ThrowsArgumentNullException()
        {
            // Act & Assert
            var exception = Assert.Throws<ArgumentNullException>(() => new ContentController(null!));
            Assert.Equal("mediator", exception.ParamName);
        }

        [Fact]
        public void Constructor_WithValidMediator_CreatesInstance()
        {
            // Arrange
            var mediator = new Mock<IMediator>();

            // Act
            var controller = new ContentController(mediator.Object);

            // Assert
            Assert.NotNull(controller);
        }

        #endregion

        #region UploadFile Tests

        [Fact]
        public async Task UploadFile_WithValidFile_Returns201Created()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var contentId = Guid.NewGuid();
            var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 1024);

            var contentDto = new ContentDto(
                contentId,
                entityId,
                "images",
                "/images/test.jpg",
                "Image",
                1024
            );

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(contentDto));

            // Act
            var result = await _controller.UploadFile(entityId, mockFile.Object, CancellationToken.None);

            // Assert
            var createdResult = Assert.IsType<CreatedAtActionResult>(result);
            Assert.Equal(201, createdResult.StatusCode);
            Assert.Equal(nameof(ContentController.GetContent), createdResult.ActionName);

            var response = Assert.IsType<ContentResponse>(createdResult.Value);
            Assert.Equal(contentId, response.Id);
            Assert.Equal(entityId, response.EntityId);
            Assert.Equal("images", response.EntityType);
            Assert.Equal("/images/test.jpg", response.Url);
            Assert.Equal("Image", response.ContentType);
            Assert.Equal(1024, response.FileSize);
        }

        [Fact]
        public async Task UploadFile_WithValidFile_PassesCorrectCommandParameters()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 1024);
            UploadFileCommand? capturedCommand = null;

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()))
                .Callback<IRequest<Result<ContentDto>>, CancellationToken>((cmd, _) =>
                    capturedCommand = cmd as UploadFileCommand)
                .ReturnsAsync(Result.Success(new ContentDto(
                    Guid.NewGuid(), entityId, "images", "/images/test.jpg", "Image", 1024)));

            // Act
            await _controller.UploadFile(entityId, mockFile.Object, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedCommand);
            Assert.Equal(entityId, capturedCommand!.EntityId);
            Assert.Equal(_testUserId, capturedCommand.UserId);
            Assert.Equal(mockFile.Object, capturedCommand.File);
        }

        [Fact]
        public async Task UploadFile_WhenValidationFails_ReturnsBadRequest()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var mockFile = CreateMockFormFile("test.exe", "application/x-msdownload", 1024);

            var failedResult = Result.Failure<ContentDto>(
                Error.Validation("Content.InvalidFileType", "File type not allowed."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.UploadFile(entityId, mockFile.Object, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, objectResult.StatusCode);
        }

        [Fact]
        public async Task UploadFile_WhenStorageFails_ReturnsInternalServerError()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 1024);

            var failedResult = Result.Failure<ContentDto>(
                Error.Storage("Content.StorageFailed", "Failed to upload file to storage."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.UploadFile(entityId, mockFile.Object, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, objectResult.StatusCode);
        }

        [Fact]
        public async Task UploadFile_WithDifferentFileTypes_ReturnsAppropriateEntityType()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var contentId = Guid.NewGuid();
            var mockFile = CreateMockFormFile("document.pdf", "application/pdf", 2048);

            var contentDto = new ContentDto(
                contentId,
                entityId,
                "documents",
                "/documents/document.pdf",
                "Document",
                2048
            );

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadFileCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(contentDto));

            // Act
            var result = await _controller.UploadFile(entityId, mockFile.Object, CancellationToken.None);

            // Assert
            var createdResult = Assert.IsType<CreatedAtActionResult>(result);
            var response = Assert.IsType<ContentResponse>(createdResult.Value);
            Assert.Equal("documents", response.EntityType);
            Assert.Equal("Document", response.ContentType);
        }

        #endregion

        #region GetContent Tests

        [Fact]
        public async Task GetContent_WithValidId_ReturnsOk()
        {
            // Arrange
            var contentId = Guid.NewGuid();
            var entityId = Guid.NewGuid();
            var contentDto = new ContentDto(
                contentId,
                entityId,
                "images",
                "/images/test.jpg",
                "Image",
                1024
            );

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<GetContentQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(contentDto));

            // Act
            var result = await _controller.GetContent(contentId, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, okResult.StatusCode);

            var response = Assert.IsType<ContentResponse>(okResult.Value);
            Assert.Equal(contentId, response.Id);
            Assert.Equal(entityId, response.EntityId);
            Assert.Equal("images", response.EntityType);
            Assert.Equal("/images/test.jpg", response.Url);
        }

        [Fact]
        public async Task GetContent_PassesCorrectIdToQuery()
        {
            // Arrange
            var contentId = Guid.NewGuid();
            GetContentQuery? capturedQuery = null;

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<GetContentQuery>(), It.IsAny<CancellationToken>()))
                .Callback<IRequest<Result<ContentDto>>, CancellationToken>((query, _) =>
                    capturedQuery = query as GetContentQuery)
                .ReturnsAsync(Result.Success(new ContentDto(
                    contentId, Guid.NewGuid(), "images", "/images/test.jpg", "Image", 1024)));

            // Act
            await _controller.GetContent(contentId, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedQuery);
            Assert.Equal(contentId, capturedQuery!.ContentId);
        }

        [Fact]
        public async Task GetContent_WhenContentNotFound_ReturnsNotFound()
        {
            // Arrange
            var contentId = Guid.NewGuid();
            var failedResult = Result.Failure<ContentDto>(
                Error.NotFound("Content.NotFound", $"Content with ID {contentId} not found."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<GetContentQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.GetContent(contentId, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(404, objectResult.StatusCode);
        }

        #endregion

        #region DeleteContent Tests

        [Fact]
        public async Task DeleteContent_WithValidId_ReturnsNoContent()
        {
            // Arrange
            var contentId = Guid.NewGuid();

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<DeleteContentCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            // Act
            var result = await _controller.DeleteContent(contentId, CancellationToken.None);

            // Assert
            var noContentResult = Assert.IsType<NoContentResult>(result);
            Assert.Equal(204, noContentResult.StatusCode);
        }

        [Fact]
        public async Task DeleteContent_PassesCorrectIdToCommand()
        {
            // Arrange
            var contentId = Guid.NewGuid();
            DeleteContentCommand? capturedCommand = null;

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<DeleteContentCommand>(), It.IsAny<CancellationToken>()))
                .Callback<IRequest<Result>, CancellationToken>((cmd, _) =>
                    capturedCommand = cmd as DeleteContentCommand)
                .ReturnsAsync(Result.Success());

            // Act
            await _controller.DeleteContent(contentId, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedCommand);
            Assert.Equal(contentId, capturedCommand!.ContentId);
        }

        [Fact]
        public async Task DeleteContent_WhenContentNotFound_ReturnsNotFound()
        {
            // Arrange
            var contentId = Guid.NewGuid();
            var failedResult = Result.Failure(
                Error.NotFound("Content.NotFound", $"Content with ID {contentId} not found."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<DeleteContentCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.DeleteContent(contentId, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(404, objectResult.StatusCode);
        }

        #endregion

        #region InitChunkSession Tests

        [Fact]
        public async Task InitChunkSession_WithValidRequest_Returns201Created()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var expiresAt = DateTime.UtcNow.AddHours(24);
            var request = new InitChunkSessionRequest(
                entityId,
                "large-video.mp4",
                "video/mp4",
                TotalChunks: 10,
                TotalSize: 52428800,
                ChunkSize: 5242880
            );

            var chunkSession = new ChunkSession
            {
                Id = sessionId,
                EntityId = entityId,
                FileName = "large-video.mp4",
                TotalChunks = 10,
                TotalSize = 52428800,
                ExpiresAt = expiresAt,
                IsCompleted = false
            };

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<InitChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(chunkSession));

            // Act
            var result = await _controller.InitChunkSession(request, CancellationToken.None);

            // Assert
            var createdResult = Assert.IsType<CreatedAtActionResult>(result);
            Assert.Equal(201, createdResult.StatusCode);
            Assert.Equal(nameof(ContentController.GetChunkSessionStatus), createdResult.ActionName);

            var response = Assert.IsType<ChunkSessionResponse>(createdResult.Value);
            Assert.Equal(sessionId, response.SessionId);
            Assert.Equal(expiresAt, response.ExpiresAt);
            Assert.Equal(10, response.TotalChunks);
        }

        [Fact]
        public async Task InitChunkSession_PassesCorrectParametersToCommand()
        {
            // Arrange
            var entityId = Guid.NewGuid();
            var request = new InitChunkSessionRequest(
                entityId,
                "large-video.mp4",
                "video/mp4",
                TotalChunks: 10,
                TotalSize: 52428800,
                ChunkSize: 5242880
            );

            InitChunkSessionCommand? capturedCommand = null;
            _mediatorMock
                .Setup(m => m.Send(It.IsAny<InitChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .Callback<IRequest<Result<ChunkSession>>, CancellationToken>((cmd, _) =>
                    capturedCommand = cmd as InitChunkSessionCommand)
                .ReturnsAsync(Result.Success(new ChunkSession
                {
                    Id = Guid.NewGuid(),
                    EntityId = entityId,
                    FileName = "large-video.mp4",
                    TotalChunks = 10,
                    TotalSize = 52428800,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                }));

            // Act
            await _controller.InitChunkSession(request, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedCommand);
            Assert.Equal(entityId, capturedCommand!.EntityId);
            Assert.Equal("large-video.mp4", capturedCommand.FileName);
            Assert.Equal("video/mp4", capturedCommand.ContentType);
            Assert.Equal(10, capturedCommand.TotalChunks);
            Assert.Equal(52428800, capturedCommand.TotalSize);
            Assert.Equal(5242880, capturedCommand.ChunkSize);
        }

        [Fact]
        public async Task InitChunkSession_WhenValidationFails_ReturnsBadRequest()
        {
            // Arrange
            var request = new InitChunkSessionRequest(
                Guid.NewGuid(),
                "large-video.mp4",
                "video/mp4",
                TotalChunks: 0, // Invalid
                TotalSize: 52428800,
                ChunkSize: 5242880
            );

            var failedResult = Result.Failure<ChunkSession>(
                Error.Validation("Content.InvalidChunkParams", "Invalid chunk session parameters."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<InitChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.InitChunkSession(request, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, objectResult.StatusCode);
        }

        [Fact]
        public async Task InitChunkSession_WhenMetadataValidationFails_ReturnsBadRequest()
        {
            // Arrange
            var request = new InitChunkSessionRequest(
                Guid.NewGuid(),
                "malicious.exe",
                "application/x-msdownload",
                TotalChunks: 10,
                TotalSize: 52428800,
                ChunkSize: 5242880
            );

            var failedResult = Result.Failure<ChunkSession>(
                Error.Validation("Content.MetadataValidationFailed", "File type not allowed."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<InitChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.InitChunkSession(request, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, objectResult.StatusCode);
        }

        #endregion

        #region UploadChunk Tests

        [Fact]
        public async Task UploadChunk_WithValidChunk_ReturnsOk()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var chunkIndex = 0;
            var mockChunkFile = CreateMockFormFile("chunk_0", "application/octet-stream", 5242880);

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadChunkCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            // Act
            var result = await _controller.UploadChunk(sessionId, chunkIndex, mockChunkFile.Object, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, okResult.StatusCode);
        }

        [Fact]
        public async Task UploadChunk_ReturnsCorrectSuccessMessage()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var chunkIndex = 5;
            var mockChunkFile = CreateMockFormFile("chunk_5", "application/octet-stream", 5242880);

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadChunkCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            // Act
            var result = await _controller.UploadChunk(sessionId, chunkIndex, mockChunkFile.Object, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var value = okResult.Value;

            // Check that the response contains the expected message pattern
            var messageProperty = value?.GetType().GetProperty("message");
            Assert.NotNull(messageProperty);
            var message = messageProperty.GetValue(value) as string;
            Assert.Contains($"Chunk {chunkIndex}", message);
            Assert.Contains(sessionId.ToString(), message);
        }

        [Fact]
        public async Task UploadChunk_PassesCorrectParametersToCommand()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var chunkIndex = 3;
            var mockChunkFile = CreateMockFormFile("chunk_3", "application/octet-stream", 5242880);
            UploadChunkCommand? capturedCommand = null;

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadChunkCommand>(), It.IsAny<CancellationToken>()))
                .Callback<IRequest<Result>, CancellationToken>((cmd, _) =>
                    capturedCommand = cmd as UploadChunkCommand)
                .ReturnsAsync(Result.Success());

            // Act
            await _controller.UploadChunk(sessionId, chunkIndex, mockChunkFile.Object, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedCommand);
            Assert.Equal(sessionId, capturedCommand!.SessionId);
            Assert.Equal(chunkIndex, capturedCommand.ChunkIndex);
            Assert.Equal(mockChunkFile.Object, capturedCommand.ChunkFile);
        }

        [Fact]
        public async Task UploadChunk_WhenChunkIsEmpty_ReturnsBadRequest()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var chunkIndex = 0;
            var mockChunkFile = CreateMockFormFile("chunk_0", "application/octet-stream", 0);

            var failedResult = Result.Failure(
                Error.Validation("Content.InvalidChunk", "Invalid or empty chunk file."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadChunkCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.UploadChunk(sessionId, chunkIndex, mockChunkFile.Object, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, objectResult.StatusCode);
        }

        [Fact]
        public async Task UploadChunk_WhenChunkIndexIsNegative_ReturnsBadRequest()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var chunkIndex = -1;
            var mockChunkFile = CreateMockFormFile("chunk_neg", "application/octet-stream", 5242880);

            var failedResult = Result.Failure(
                Error.Validation("Content.InvalidChunkIndex", "Chunk index cannot be negative."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadChunkCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.UploadChunk(sessionId, chunkIndex, mockChunkFile.Object, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, objectResult.StatusCode);
        }

        [Fact]
        public async Task UploadChunk_WhenSessionNotFound_ReturnsNotFound()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var chunkIndex = 0;
            var mockChunkFile = CreateMockFormFile("chunk_0", "application/octet-stream", 5242880);

            var failedResult = Result.Failure(
                Error.NotFound("Content.SessionNotFound", "Session not found."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadChunkCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.UploadChunk(sessionId, chunkIndex, mockChunkFile.Object, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(404, objectResult.StatusCode);
        }

        [Fact]
        public async Task UploadChunk_WhenChunkIndexOutOfRange_ReturnsBadRequest()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var chunkIndex = 100; // Out of range
            var mockChunkFile = CreateMockFormFile("chunk_100", "application/octet-stream", 5242880);

            var failedResult = Result.Failure(
                Error.Validation("Content.ChunkOutOfRange", "Chunk index is out of range."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<UploadChunkCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.UploadChunk(sessionId, chunkIndex, mockChunkFile.Object, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, objectResult.StatusCode);
        }

        #endregion

        #region CompleteChunkSession Tests

        [Fact]
        public async Task CompleteChunkSession_WithValidSession_Returns201Created()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var contentId = Guid.NewGuid();
            var entityId = Guid.NewGuid();

            var contentDto = new ContentDto(
                contentId,
                entityId,
                "videos",
                "/videos/completed.mp4",
                "Video",
                52428800
            );

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<CompleteChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(contentDto));

            // Act
            var result = await _controller.CompleteChunkSession(sessionId, CancellationToken.None);

            // Assert
            var createdResult = Assert.IsType<CreatedAtActionResult>(result);
            Assert.Equal(201, createdResult.StatusCode);
            Assert.Equal(nameof(ContentController.GetContent), createdResult.ActionName);

            var response = Assert.IsType<ContentResponse>(createdResult.Value);
            Assert.Equal(contentId, response.Id);
            Assert.Equal(entityId, response.EntityId);
            Assert.Equal("videos", response.EntityType);
            Assert.Equal("/videos/completed.mp4", response.Url);
        }

        [Fact]
        public async Task CompleteChunkSession_PassesCorrectParametersToCommand()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            CompleteChunkSessionCommand? capturedCommand = null;

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<CompleteChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .Callback<IRequest<Result<ContentDto>>, CancellationToken>((cmd, _) =>
                    capturedCommand = cmd as CompleteChunkSessionCommand)
                .ReturnsAsync(Result.Success(new ContentDto(
                    Guid.NewGuid(), Guid.NewGuid(), "videos", "/videos/completed.mp4", "Video", 52428800)));

            // Act
            await _controller.CompleteChunkSession(sessionId, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedCommand);
            Assert.Equal(sessionId, capturedCommand!.SessionId);
            Assert.Equal(_testUserId, capturedCommand.UserId);
        }

        [Fact]
        public async Task CompleteChunkSession_WhenSessionNotFound_ReturnsNotFound()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var failedResult = Result.Failure<ContentDto>(
                Error.NotFound("Content.SessionNotFound", "Session not found."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<CompleteChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.CompleteChunkSession(sessionId, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(404, objectResult.StatusCode);
        }

        [Fact]
        public async Task CompleteChunkSession_WhenChunksIncomplete_ReturnsBadRequest()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var failedResult = Result.Failure<ContentDto>(
                Error.Validation("Content.InvalidOperation", "Not all chunks have been uploaded."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<CompleteChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.CompleteChunkSession(sessionId, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, objectResult.StatusCode);
        }

        [Fact]
        public async Task CompleteChunkSession_WhenTimeout_ReturnsTimeout()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var failedResult = Result.Failure<ContentDto>(
                Error.Timeout("Content.Timeout", "Session completion timed out."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<CompleteChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.CompleteChunkSession(sessionId, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(408, objectResult.StatusCode);
        }

        [Fact]
        public async Task CompleteChunkSession_WhenForbidden_ReturnsForbidden()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var failedResult = Result.Failure<ContentDto>(
                Error.Forbidden("Content.Forbidden", "Access denied."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<CompleteChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.CompleteChunkSession(sessionId, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(403, objectResult.StatusCode);
        }

        [Fact]
        public async Task CompleteChunkSession_WhenCompletionFails_ReturnsInternalServerError()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var failedResult = Result.Failure<ContentDto>(
                Error.Internal("Content.CompletionFailed", "Failed to finalize uploaded content."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<CompleteChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.CompleteChunkSession(sessionId, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, objectResult.StatusCode);
        }

        [Fact]
        public async Task CompleteChunkSession_WithDifferentContentTypes_ReturnsCorrectResponse()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var contentId = Guid.NewGuid();
            var entityId = Guid.NewGuid();

            var contentDto = new ContentDto(
                contentId,
                entityId,
                "documents",
                "/documents/large-doc.pdf",
                "Document",
                10485760
            );

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<CompleteChunkSessionCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(contentDto));

            // Act
            var result = await _controller.CompleteChunkSession(sessionId, CancellationToken.None);

            // Assert
            var createdResult = Assert.IsType<CreatedAtActionResult>(result);
            var response = Assert.IsType<ContentResponse>(createdResult.Value);
            Assert.Equal("documents", response.EntityType);
            Assert.Equal("Document", response.ContentType);
            Assert.Equal(10485760, response.FileSize);
        }

        #endregion

        #region GetChunkSessionStatus Tests

        [Fact]
        public async Task GetChunkSessionStatus_WithValidSession_ReturnsOk()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var entityId = Guid.NewGuid();
            var expiresAt = DateTime.UtcNow.AddHours(24);

            var chunkSession = new ChunkSession
            {
                Id = sessionId,
                EntityId = entityId,
                FileName = "large-video.mp4",
                TotalChunks = 10,
                TotalSize = 52428800,
                ExpiresAt = expiresAt,
                IsCompleted = false,
                UploadedChunks = new HashSet<int> { 0, 1, 2 }
            };

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<GetChunkSessionStatusQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(chunkSession));

            // Act
            var result = await _controller.GetChunkSessionStatus(sessionId, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, okResult.StatusCode);

            var response = Assert.IsType<ChunkSessionResponse>(okResult.Value);
            Assert.Equal(sessionId, response.SessionId);
            Assert.Equal(expiresAt, response.ExpiresAt);
            Assert.Equal(10, response.TotalChunks);
        }

        [Fact]
        public async Task GetChunkSessionStatus_PassesCorrectIdToQuery()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            GetChunkSessionStatusQuery? capturedQuery = null;

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<GetChunkSessionStatusQuery>(), It.IsAny<CancellationToken>()))
                .Callback<IRequest<Result<ChunkSession>>, CancellationToken>((query, _) =>
                    capturedQuery = query as GetChunkSessionStatusQuery)
                .ReturnsAsync(Result.Success(new ChunkSession
                {
                    Id = sessionId,
                    EntityId = Guid.NewGuid(),
                    FileName = "test.mp4",
                    TotalChunks = 5,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                }));

            // Act
            await _controller.GetChunkSessionStatus(sessionId, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedQuery);
            Assert.Equal(sessionId, capturedQuery!.SessionId);
        }

        [Fact]
        public async Task GetChunkSessionStatus_WhenSessionNotFound_ReturnsNotFound()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var failedResult = Result.Failure<ChunkSession>(
                Error.NotFound("Content.SessionNotFound", $"Session {sessionId} not found."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<GetChunkSessionStatusQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.GetChunkSessionStatus(sessionId, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(404, objectResult.StatusCode);
        }

        [Fact]
        public async Task GetChunkSessionStatus_WhenSessionExpired_ReturnsAppropriateError()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var failedResult = Result.Failure<ChunkSession>(
                Error.NotFound("Content.SessionExpired", "Session has expired."));

            _mediatorMock
                .Setup(m => m.Send(It.IsAny<GetChunkSessionStatusQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failedResult);

            // Act
            var result = await _controller.GetChunkSessionStatus(sessionId, CancellationToken.None);

            // Assert
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(404, objectResult.StatusCode);
        }

        #endregion

        #region Helper Methods

        private static Mock<IFormFile> CreateMockFormFile(string fileName, string contentType, long length)
        {
            var mockFile = new Mock<IFormFile>();
            var content = new byte[length > 0 ? length : 1];
            var stream = new MemoryStream(content);

            mockFile.Setup(f => f.FileName).Returns(fileName);
            mockFile.Setup(f => f.ContentType).Returns(contentType);
            mockFile.Setup(f => f.Length).Returns(length);
            mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
            mockFile.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Returns((Stream target, CancellationToken _) =>
                {
                    stream.Position = 0;
                    return stream.CopyToAsync(target);
                });

            return mockFile;
        }

        #endregion
    }
}
