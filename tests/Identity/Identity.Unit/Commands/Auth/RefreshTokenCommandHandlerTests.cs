using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Haworks.Identity.Application;
using Haworks.Identity.Application.Interfaces;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain.Interfaces;
using Haworks.Identity.UnitTests.Helpers;
using Haworks.BuildingBlocks.Audit;
using Haworks.BuildingBlocks.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Identity.UnitTests.Commands.Auth;

public class RefreshTokenCommandHandlerTests : TestBase
{
    private readonly Mock<UserManager<User>> _userManagerMock;
    private readonly Mock<IRefreshTokenRepository> _refreshTokenRepoMock;
    private readonly Mock<IRefreshTokenService> _refreshTokenServiceMock;
    private readonly Mock<IJwtTokenService> _jwtTokenServiceMock;
    private readonly Mock<IAuditLogger> _auditLoggerMock;

    public RefreshTokenCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        var userStoreMock = new Mock<IUserStore<User>>();
        _userManagerMock = new Mock<UserManager<User>>(
            userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _refreshTokenRepoMock = new Mock<IRefreshTokenRepository>();
        _refreshTokenServiceMock = new Mock<IRefreshTokenService>();
        _jwtTokenServiceMock = new Mock<IJwtTokenService>();
        _auditLoggerMock = new Mock<IAuditLogger>();
        _auditLoggerMock
            .Setup(a => a.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Handle_WithDeactivatedUser_ReturnsAccountDeactivatedError()
    {
        // Arrange
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "testuser",
            Email = "test@example.com",
            IsActive = false
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id)
        }));

        _jwtTokenServiceMock
            .Setup(j => j.ValidateToken(It.IsAny<string>(), false))
            .Returns(principal);

        _userManagerMock
            .Setup(um => um.FindByIdAsync(user.Id))
            .ReturnsAsync(user);

        var handler = CreateHandler();
        var httpContext = new DefaultHttpContext();
        var command = new RefreshTokenCommand("expired-access-token", "valid-refresh-token", httpContext);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("Auth.AccountDeactivated", result.Error.Code);
    }

    private RefreshTokenCommandHandler CreateHandler()
    {
        var authOptions = new AuthOptions { TokenExpiryMinutes = 15 };
        return new RefreshTokenCommandHandler(
            _userManagerMock.Object,
            _refreshTokenRepoMock.Object,
            _refreshTokenServiceMock.Object,
            _jwtTokenServiceMock.Object,
            _auditLoggerMock.Object,
            LoggerFactory.CreateLogger<RefreshTokenCommandHandler>(),
            Options.Create(authOptions));
    }
}
