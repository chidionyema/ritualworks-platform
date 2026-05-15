using Haworks.Identity.Api.Controllers;
using Haworks.Identity.Api.Models;
using Haworks.Identity.Application.Commands.Users;
using Haworks.Identity.Application.Queries.Users;
using Haworks.Identity.Application.DTOs;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Testing;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit.Abstractions;
using Xunit;

namespace Haworks.Identity.UnitTests.Controllers;

public class UserProfileControllerTests : TestBase
{
    private readonly Mock<IMediator> _mediatorMock;
    private readonly Mock<ICurrentUserService> _currentUserServiceMock;
    private readonly UserProfileController _controller;

    public UserProfileControllerTests(ITestOutputHelper output) : base(output)
    {
        _mediatorMock = new Mock<IMediator>();
        _currentUserServiceMock = new Mock<ICurrentUserService>();
        _controller = new UserProfileController(_mediatorMock.Object, _currentUserServiceMock.Object);
    }

    [Fact]
    public async Task GetProfile_WithValidUser_ReturnsOk()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns("user-id");
        var dto = new UserProfileDto { FirstName = "John" };

        _mediatorMock
            .Setup(m => m.Send(
                It.Is<GetUserProfileQuery>(q => q.UserId == "user-id"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));

        var result = await _controller.GetProfile(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        _mediatorMock.Verify(
            m => m.Send(
                It.Is<GetUserProfileQuery>(q => q.UserId == "user-id"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
