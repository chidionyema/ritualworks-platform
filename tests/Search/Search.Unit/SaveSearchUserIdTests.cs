using System.Security.Claims;
using FluentAssertions;
using Haworks.Search.Api.Controllers;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Haworks.Search.Unit;

public sealed class SaveSearchUserIdTests
{
    [Fact]
    public async Task SaveSearch_uses_authenticated_userId_not_request_body()
    {
        // Arrange
        var mockIndex = new Mock<ISearchIndex>();
        var mockLogger = new Mock<ILogger<SearchController>>();
        var controller = new SearchController(mockIndex.Object, mockLogger.Object);

        var expectedUserId = "auth-user-42";
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, expectedUserId) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        string? capturedUserId = null;
        mockIndex
            .Setup(x => x.RegisterSavedSearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, SearchQuery, CancellationToken>((_, userId, _, _) => capturedUserId = userId)
            .Returns(Task.CompletedTask);

        var query = new SearchQuery { Query = "test" };

        // Act
        var result = await controller.SaveSearch(query);

        // Assert
        capturedUserId.Should().Be(expectedUserId);
        result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task SaveSearch_returns_unauthorized_when_no_user_identity()
    {
        // Arrange
        var mockIndex = new Mock<ISearchIndex>();
        var mockLogger = new Mock<ILogger<SearchController>>();
        var controller = new SearchController(mockIndex.Object, mockLogger.Object);

        // No claims at all
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var query = new SearchQuery { Query = "test" };

        // Act
        var result = await controller.SaveSearch(query);

        // Assert
        result.Should().BeOfType<UnauthorizedObjectResult>();
        mockIndex.Verify(
            x => x.RegisterSavedSearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
