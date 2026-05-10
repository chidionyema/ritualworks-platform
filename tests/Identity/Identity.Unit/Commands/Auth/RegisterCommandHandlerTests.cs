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

public class RegisterCommandHandlerTests : TestBase
{
    private readonly Mock<UserManager<User>> _userManagerMock;
    private readonly Mock<IJwtTokenService> _jwtTokenServiceMock;
    private readonly Mock<IAuditLogger> _auditLoggerMock;
    private readonly JwtOptions _jwtOptions;

    public RegisterCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        var userStoreMock = new Mock<IUserStore<User>>();
        _userManagerMock = new Mock<UserManager<User>>(
            userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _jwtTokenServiceMock = new Mock<IJwtTokenService>();
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
    public async Task Handle_WithValidInput_ReturnsSuccessWithAuthResponse()
    {
        var httpContext = new DefaultHttpContext();
        var command = new RegisterCommand("newuser", "newuser@example.com", "ValidPassword123!", httpContext);

        SetupSuccessfulRegistration();
        var handler = CreateHandler();

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("newuser", result.Value.Username);
        Assert.Equal("newuser@example.com", result.Value.Email);
    }

    private RegisterCommandHandler CreateHandler()
    {
        return new RegisterCommandHandler(
            _userManagerMock.Object,
            _jwtTokenServiceMock.Object,
            Options.Create(_jwtOptions),
            _auditLoggerMock.Object,
            LoggerFactory.CreateLogger<RegisterCommandHandler>());
    }

    private void SetupSuccessfulRegistration()
    {
        _userManagerMock
            .Setup(um => um.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
            .Callback<User, string>((u, _) => u.Id = Guid.NewGuid().ToString())
            .ReturnsAsync(IdentityResult.Success);

        _userManagerMock
            .Setup(um => um.AddToRoleAsync(It.IsAny<User>(), "ContentUploader"))
            .ReturnsAsync(IdentityResult.Success);

        _userManagerMock
            .Setup(um => um.AddClaimAsync(It.IsAny<User>(), It.IsAny<Claim>()))
            .ReturnsAsync(IdentityResult.Success);

        _jwtTokenServiceMock
            .Setup(j => j.GenerateTokenAsync(It.IsAny<User>(), It.IsAny<DateTime>()))
            .ReturnsAsync(CreateTestToken());

        _jwtTokenServiceMock
            .Setup(j => j.SetSecureCookie(It.IsAny<HttpContext>(), It.IsAny<JwtSecurityToken>()));
    }

    private static JwtSecurityToken CreateTestToken()
    {
        return new JwtSecurityToken(
            issuer: "test-issuer",
            audience: "test-audience",
            claims: new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) },
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(new byte[32]),
                SecurityAlgorithms.HmacSha256));
    }
}
