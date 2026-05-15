using Haworks.Identity.Api.Controllers;
using Haworks.Identity.Api.Models;
using Haworks.Identity.Application;
using Haworks.Identity.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Testing;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using Moq;
using Xunit.Abstractions;
using Xunit;

namespace Haworks.Identity.UnitTests.Controllers;

public class AuthenticationControllerTests : TestBase
{
    private readonly Mock<IMediator> _mediatorMock;
    private readonly Mock<IAntiforgery> _antiforgeryMock;
    private readonly AuthenticationController _controller;

    public AuthenticationControllerTests(ITestOutputHelper output) : base(output)
    {
        _mediatorMock = new Mock<IMediator>();
        _antiforgeryMock = new Mock<IAntiforgery>();
        _controller = new AuthenticationController(_mediatorMock.Object, _antiforgeryMock.Object);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsOk()
    {
        var request = new LoginRequest("user", "pass");
        var authResponseDto = new AuthResponseDto
        {
            Token = "token",
            RefreshToken = "refresh",
            Expires = DateTime.UtcNow.AddMinutes(15),
            Username = "user",
            Email = "email",
            UserId = "id",
            Message = "Success"
        };

        _mediatorMock
            .Setup(m => m.Send(It.Is<LoginCommand>(c =>
                c.Username == request.Username &&
                c.Password == request.Password), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(authResponseDto));

        var result = await _controller.Login(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        _mediatorMock.Verify(
            m => m.Send(
                It.Is<LoginCommand>(c =>
                    c.Username == request.Username &&
                    c.Password == request.Password),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
