using FluentAssertions;
using Haworks.Media.Api.Application;
using Haworks.Media.Api.Infrastructure;
using Haworks.Media.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Haworks.Media.Unit;

public class InitiateUploadTests
{
    private readonly MediaDbContext _context;
    private readonly Mock<IS3Service> _s3ServiceMock;
    private readonly InitiateUploadHandler _handler;

    public InitiateUploadTests()
    {
        var options = new DbContextOptionsBuilder<MediaDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new MediaDbContext(options);
        _s3ServiceMock = new Mock<IS3Service>();
        _handler = new InitiateUploadHandler(_context, _s3ServiceMock.Object);
    }

    [Fact]
    public async Task Handle_NewFile_ShouldCreateMediaAndReturnUrl()
    {
        // Arrange
        var command = new InitiateUploadCommand("test.png", new string('a', 64), 1024, "image/png");
        _s3ServiceMock.Setup(x => x.GeneratePreSignedUrl(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("http://upload-url");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.UploadUrl.Should().Be("http://upload-url");
        result.Value.AlreadyExists.Should().BeFalse();
        
        var file = await _context.MediaFiles.FindAsync(result.Value.Id);
        file.Should().NotBeNull();
        file!.Status.Should().Be(MediaStatus.Pending);
    }

    [Fact]
    public async Task Handle_DuplicateHash_ShouldReturnExistingMediaId()
    {
        // Arrange
        var hash = new string('b', 64);
        var existingFile = MediaFile.Create("old.png", hash, 512, "image/png");
        _context.MediaFiles.Add(existingFile);
        await _context.SaveChangesAsync();

        var command = new InitiateUploadCommand("new.png", hash, 1024, "image/png");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(existingFile.Id);
        result.Value.AlreadyExists.Should().BeTrue();
        result.Value.UploadUrl.Should().BeEmpty();
    }
}
