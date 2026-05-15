using FluentAssertions;
using Haworks.Localization.Api.Application;
using Haworks.Localization.Api.Domain;
using Haworks.Localization.Api.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Haworks.Localization.Unit;

public class GetTranslationQueryHandlerTests
{
    private readonly LocalizationDbContext _dbContext;
    private readonly Mock<IPublishEndpoint> _publishEndpointMock;
    private readonly GetTranslationQueryHandler _handler;

    public GetTranslationQueryHandlerTests()
    {
        var options = new DbContextOptionsBuilder<LocalizationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new LocalizationDbContext(options);
        _publishEndpointMock = new Mock<IPublishEndpoint>();
        _handler = new GetTranslationQueryHandler(_dbContext, _publishEndpointMock.Object);
    }

    [Fact]
    public async Task Handle_ShouldReturnTranslation_WhenKeyAndLocaleExist()
    {
        // Arrange
        var translation = new Translation("welcome", new Dictionary<string, string> { { "en-US", "Welcome" } });
        _dbContext.Translations.Add(translation);
        await _dbContext.SaveChangesAsync();

        var query = new GetTranslationQuery("welcome", "en-US");

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("Welcome");
    }

    [Fact]
    public async Task Handle_ShouldReturnFallback_WhenSpecificLocaleMissingButParentExists()
    {
        // Arrange
        var translation = new Translation("welcome", new Dictionary<string, string> { { "fr", "Bienvenue" } });
        _dbContext.Translations.Add(translation);
        await _dbContext.SaveChangesAsync();

        var query = new GetTranslationQuery("welcome", "fr-CA");

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("Bienvenue");
    }

    [Fact]
    public async Task Handle_ShouldReturnDefaultFallback_WhenRequestedLocaleMissingButEnUSExists()
    {
        // Arrange
        var translation = new Translation("welcome", new Dictionary<string, string> { { "en-US", "Welcome" } });
        _dbContext.Translations.Add(translation);
        await _dbContext.SaveChangesAsync();

        var query = new GetTranslationQuery("welcome", "de-DE");

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("Welcome");
    }

    [Fact]
    public async Task Handle_ShouldReturnFailureAndPublishEvent_WhenKeyNotFound()
    {
        // Arrange
        var query = new GetTranslationQuery("missing", "en-US");

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        _publishEndpointMock.Verify(x => x.Publish(It.Is<TranslationMissingEvent>(e => e.Key == "missing"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
