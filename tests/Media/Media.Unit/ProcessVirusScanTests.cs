using FluentAssertions;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Media.Api.Application;
using Haworks.Media.Api.Infrastructure;
using Haworks.Media.Api.Infrastructure.Processing;
using Haworks.Media.Api.Domain;
using Haworks.Media.Api.Options;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Haworks.Media.Unit;

public class ProcessVirusScanTests
{
    private const string OwnerId = "test-owner-456";
    // SHA-256 of bytes { 0x01, 0x02, 0x03 } used in the mock S3 download
    private const string TestFileHash = "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81";
    private readonly MediaDbContext _context;
    private readonly Mock<IVirusScanner> _scannerMock;
    private readonly Mock<ICurrentUserService> _currentUserMock;
    private readonly Mock<IS3Service> _s3Mock;
    private readonly ProcessVirusScanHandler _handler;

    public ProcessVirusScanTests()
    {
        var options = new DbContextOptionsBuilder<MediaDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _context = new MediaDbContext(options);
        _scannerMock = new Mock<IVirusScanner>();
        _currentUserMock = new Mock<ICurrentUserService>();
        _currentUserMock.Setup(x => x.UserId).Returns(OwnerId);
        _s3Mock = new Mock<IS3Service>();
        _s3Mock.Setup(x => x.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));
        var orchestrator = new MediaProcessingOrchestrator(
            Array.Empty<IMediaProcessor>(),
            Options.Create(new TranscodeOptions()),
            NullLogger<MediaProcessingOrchestrator>.Instance);
        var publisherMock = new Mock<IPublishEndpoint>();
        _handler = new ProcessVirusScanHandler(
            _context, _scannerMock.Object, _currentUserMock.Object, _s3Mock.Object,
            orchestrator, publisherMock.Object);
    }

    [Fact]
    public async Task Handle_CleanFile_ShouldUpdateStatusToActive()
    {
        var mediaFile = MediaFile.Create("test.png", TestFileHash, 1024, "image/png", OwnerId);
        _context.MediaFiles.Add(mediaFile);
        await _context.SaveChangesAsync();

        _scannerMock.Setup(x => x.ScanAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _handler.Handle(new ProcessVirusScanCommand(mediaFile.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var updatedFile = await _context.MediaFiles.FindAsync(mediaFile.Id);
        updatedFile!.Status.Should().Be(MediaStatus.Active);
    }

    [Fact]
    public async Task Handle_InfectedFile_ShouldUpdateStatusToRejected()
    {
        var mediaFile = MediaFile.Create("bad.exe", TestFileHash, 2048, "application/octet-stream", OwnerId);
        _context.MediaFiles.Add(mediaFile);
        await _context.SaveChangesAsync();

        _scannerMock.Setup(x => x.ScanAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _handler.Handle(new ProcessVirusScanCommand(mediaFile.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var updatedFile = await _context.MediaFiles.FindAsync(mediaFile.Id);
        updatedFile!.Status.Should().Be(MediaStatus.Rejected);
    }

    [Fact]
    public async Task Handle_InvalidId_ShouldReturnFailure()
    {
        var result = await _handler.Handle(new ProcessVirusScanCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Media.NotFound");
    }

    [Fact]
    public async Task Handle_WrongOwner_ShouldReturnForbidden()
    {
        var mediaFile = MediaFile.Create("test.png", new string('e', 64), 1024, "image/png", "other-owner");
        _context.MediaFiles.Add(mediaFile);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new ProcessVirusScanCommand(mediaFile.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Media.Forbidden");
    }

    [Fact]
    public async Task Handle_NoUserId_ShouldReturnUnauthorized()
    {
        _currentUserMock.Setup(x => x.UserId).Returns(string.Empty);

        var result = await _handler.Handle(new ProcessVirusScanCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Media.Unauthorized");
    }
}
