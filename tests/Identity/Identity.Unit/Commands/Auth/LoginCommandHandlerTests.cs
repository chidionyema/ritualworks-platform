using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Haworks.Identity.Application;
using Haworks.Identity.Application.Interfaces;
using Haworks.Identity.Application.Options;
using Haworks.Identity.Domain;
using Haworks.BuildingBlocks.Testing;
using Haworks.Identity.UnitTests.Helpers;
using Haworks.BuildingBlocks.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit.Abstractions;
using Xunit;

namespace Haworks.Identity.UnitTests.Commands.Auth;

public class LoginCommandHandlerTests : TestBase
{
    private readonly Mock<UserManager<User>> _userManagerMock;
    private readonly Mock<SignInManager<User>> _signInManagerMock;
    private readonly Mock<IJwtTokenService> _jwtTokenServiceMock;
    private readonly Mock<IRefreshTokenService> _refreshTokenServiceMock;
    private readonly Mock<IAuditLogger> _auditLoggerMock;
    private readonly JwtOptions _jwtOptions;

    public LoginCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        var userStoreMock = new Mock<IUserStore<User>>();
        _userManagerMock = new Mock<UserManager<User>>(
            userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        var contextAccessorMock = new Mock<IHttpContextAccessor>();
        var userPrincipalFactoryMock = new Mock<IUserClaimsPrincipalFactory<User>>();
        _signInManagerMock = new Mock<SignInManager<User>>(
            _userManagerMock.Object,
            contextAccessorMock.Object,
            userPrincipalFactoryMock.Object,
            null!, null!, null!, null!);

        _jwtTokenServiceMock = new Mock<IJwtTokenService>();
        _refreshTokenServiceMock = new Mock<IRefreshTokenService>();
        _auditLoggerMock = new Mock<IAuditLogger>();
        _auditLoggerMock
            .Setup(a => a.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _jwtOptions = new JwtOptions
        {
            Key = "test-key-must-be-at-least-32-characters-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience",
            TokenExpiryMinutes = 15,
            RefreshTokenExpiryDays = 7
        };
    }

    [Fact]
    public async Task Handle_WithValidCredentials_ReturnsSuccessWithAuthResponse()
    {
        var user = CreateTestUser();
        var httpContext = new DefaultHttpContext();
        var command = new LoginCommand(UnitTestConstants.Users.DefaultUsername, UnitTestConstants.Auth.ValidPassword, httpContext);

        SetupSuccessfulLogin(user);
        var handler = CreateHandler();

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(user.Id, result.Value.UserId);
        Assert.Equal(user.UserName, result.Value.Username);
        Assert.Equal(user.Email, result.Value.Email);
    }

    [Fact]
    public async Task Handle_WithValidCredentials_GeneratesJwtToken()
    {
        var user = CreateTestUser();
        var httpContext = new DefaultHttpContext();
        var command = new LoginCommand(UnitTestConstants.Users.DefaultUsername, UnitTestConstants.Auth.ValidPassword, httpContext);

        SetupSuccessfulLogin(user);
        var handler = CreateHandler();

        await handler.Handle(command, CancellationToken.None);

        _jwtTokenServiceMock.Verify(
            j => j.GenerateTokenAsync(
                It.Is<User>(u => u.Id == user.Id),
                It.IsAny<DateTime>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_WithDeactivatedUser_ReturnsAccountDeactivatedError()
    {
        var user = CreateTestUser();
        user.IsActive = false;
        var httpContext = new DefaultHttpContext();
        var command = new LoginCommand(UnitTestConstants.Users.DefaultUsername, UnitTestConstants.Auth.ValidPassword, httpContext);

        SetupSuccessfulLogin(user);
        var handler = CreateHandler();

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.AccountDeactivated", result.Error.Code);
    }

    private LoginCommandHandler CreateHandler()
    {
        return new LoginCommandHandler(
            _userManagerMock.Object,
            _signInManagerMock.Object,
            _jwtTokenServiceMock.Object,
            _refreshTokenServiceMock.Object,
            Options.Create(_jwtOptions),
            _auditLoggerMock.Object,
            LoggerFactory.CreateLogger<LoginCommandHandler>());
    }

    private static User CreateTestUser()
    {
        return new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = UnitTestConstants.Users.DefaultUsername,
            Email = UnitTestConstants.Users.DefaultEmail
        };
    }

    private static JwtSecurityToken CreateTestToken(User user, DateTime? expiry = null)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName!),
            new Claim(ClaimTypes.Email, user.Email!)
        };

        return new JwtSecurityToken(
            issuer: UnitTestConstants.Auth.TestIssuer,
            audience: UnitTestConstants.Auth.TestAudience,
            claims: claims,
            expires: expiry ?? DateTime.UtcNow.Add(UnitTestConstants.Auth.TokenExpiration),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(new byte[32]),
                SecurityAlgorithms.HmacSha256));
    }

    private static RefreshToken CreateTestRefreshToken(string userId)
    {
        return RefreshToken.Create(
            userId: userId,
            token: "test-refresh-token",
            expires: DateTime.UtcNow.Add(UnitTestConstants.Auth.RefreshTokenExpiration));
    }

    private void SetupSuccessfulLogin(User user, JwtSecurityToken? token = null)
    {
        _userManagerMock
            .Setup(um => um.FindByNameAsync(user.UserName!))
            .ReturnsAsync(user);
        _userManagerMock
            .Setup(um => um.IsLockedOutAsync(user))
            .ReturnsAsync(false);
        _userManagerMock
            .Setup(um => um.ResetAccessFailedCountAsync(user))
            .ReturnsAsync(IdentityResult.Success);
        _signInManagerMock
            .Setup(sm => sm.CheckPasswordSignInAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Success);
        _jwtTokenServiceMock
            .Setup(j => j.GenerateTokenAsync(It.IsAny<User>(), It.IsAny<DateTime>()))
            .ReturnsAsync(token ?? CreateTestToken(user));
        _refreshTokenServiceMock
            .Setup(r => r.GenerateRefreshTokenAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestRefreshToken(user.Id));
        _jwtTokenServiceMock
            .Setup(j => j.SetSecureCookie(It.IsAny<HttpContext>(), It.IsAny<JwtSecurityToken>()));
    }
}
