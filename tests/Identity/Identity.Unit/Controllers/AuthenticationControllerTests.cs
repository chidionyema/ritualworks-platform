using Haworks.Identity.Api.Controllers;
using Haworks.Identity.Api.Models;
using Haworks.Identity.Application;
using Haworks.Identity.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Testing;
using MediatR;
using Microsoft.AspNetCore.Http;
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
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
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

<<<<<<< HEAD
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        _mediatorMock.Verify(
            m => m.Send(
                It.Is<LoginCommand>(c =>
                    c.Username == request.Username &&
                    c.Password == request.Password),
                It.IsAny<CancellationToken>()),
            Times.Once);
=======
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuthResponse>(okResult.Value);
        Assert.Equal("token", response.Token);
        Assert.Equal("refresh", response.RefreshToken);
        Assert.Equal("user", response.Username);
        Assert.Equal("email", response.Email);
        Assert.Equal("id", response.UserId);
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ReturnsBadRequest()
    {
        var request = new LoginRequest("user", "wrong");

        _mediatorMock
            .Setup(m => m.Send(It.IsAny<LoginCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthResponseDto>(
                Error.Validation("Auth.InvalidCredentials", "Invalid username or password")));

        var result = await _controller.Login(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task Register_WithValidData_ReturnsCreated()
    {
        var request = new RegisterRequest("newuser", "new@email.com", "StrongPass1!");
        var authResponseDto = new AuthResponseDto
        {
            Token = "token",
            RefreshToken = "refresh",
            Expires = DateTime.UtcNow.AddMinutes(15),
            Username = "newuser",
            Email = "new@email.com",
            UserId = "new-id",
            Message = "Registered"
        };

        _mediatorMock
            .Setup(m => m.Send(It.IsAny<RegisterCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(authResponseDto));

        var result = await _controller.Register(request, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result);
        var response = Assert.IsType<AuthResponse>(createdResult.Value);
        Assert.Equal("token", response.Token);
        Assert.Equal("refresh", response.RefreshToken);
        Assert.Equal("newuser", response.Username);
        Assert.Equal("new@email.com", response.Email);
        Assert.Equal("new-id", response.UserId);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ReturnsBadRequest()
    {
        var request = new RegisterRequest("newuser", "existing@email.com", "StrongPass1!");

        _mediatorMock
            .Setup(m => m.Send(It.IsAny<RegisterCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<AuthResponseDto>(
                Error.Validation("Auth.DuplicateEmail", "Email already registered")));

        var result = await _controller.Register(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
>>>>>>> worktree-agent-a1268af7
    }
}
