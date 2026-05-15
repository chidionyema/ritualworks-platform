using FluentAssertions;
using Haworks.Media.Api.Application;
using Haworks.Media.Api.Infrastructure;
using Haworks.Media.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Haworks.Media.Unit;

public class ProcessVirusScanTests
{
    private readonly MediaDbContext _context;
    private readonly ProcessVirusScanHandler _handler;

    public ProcessVirusScanTests()
    {
        var options = new DbContextOptionsBuilder<MediaDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new MediaDbContext(options);
        _handler = new ProcessVirusScanHandler(_context);
    }

    [Fact]
    public async Task Handle_ValidId_ShouldUpdateStatusToActive()
    {
        // Arrange
        var mediaFile = MediaFile.Create("test.png", new string('c', 64), 1024, "image/png");
        _context.MediaFiles.Add(mediaFile);
        await _context.SaveChangesAsync();

        var command = new ProcessVirusScanCommand(mediaFile.Id);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        
        var updatedFile = await _context.MediaFiles.FindAsync(mediaFile.Id);
        updatedFile!.Status.Should().Be(MediaStatus.Active);
    }

    [Fact]
    public async Task Handle_InvalidId_ShouldReturnFailure()
    {
        // Arrange
        var command = new ProcessVirusScanCommand(Guid.NewGuid());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Media.NotFound");
    }
}
