using FluentAssertions;
using Haworks.Identity.Application;
using Haworks.Identity.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Haworks.Identity.Unit.Queries;

public class GetAvailableProvidersQueryHandlerTests
{
    private readonly Mock<SignInManager<User>> _signInManagerMock;
    private readonly GetAvailableProvidersQueryHandler _handler;

    public GetAvailableProvidersQueryHandlerTests()
    {
        _signInManagerMock = CreateMockSignInManager();
        _handler = new GetAvailableProvidersQueryHandler(_signInManagerMock.Object);
    }

    [Fact]
    public async Task Handle_ReturnsConfiguredProviders()
    {
        var schemes = new List<AuthenticationScheme>
        {
            new("Google", "Google", typeof(IAuthenticationHandler)),
            new("Facebook", "Facebook", typeof(IAuthenticationHandler))
        };

        _signInManagerMock.Setup(m => m.GetExternalAuthenticationSchemesAsync())
            .ReturnsAsync(schemes);

        var result = await _handler.Handle(new GetAvailableProvidersQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().Contain(p => p.Name == "Google");
    }

    private static Mock<SignInManager<User>> CreateMockSignInManager()
    {
        var userStore = new Mock<IUserStore<User>>();
        var userManager = new Mock<UserManager<User>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        return new Mock<SignInManager<User>>(userManager.Object, Mock.Of<IHttpContextAccessor>(), Mock.Of<IUserClaimsPrincipalFactory<User>>(), Mock.Of<IOptions<IdentityOptions>>(), Mock.Of<ILogger<SignInManager<User>>>(), Mock.Of<IAuthenticationSchemeProvider>(), Mock.Of<IUserConfirmation<User>>());
    }
}
