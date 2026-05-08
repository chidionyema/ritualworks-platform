using System.Security.Claims;
using FluentAssertions;
using Haworks.BuildingBlocks.Authentication;
using Microsoft.AspNetCore.Http;
using Moq;
using Moq.Protected;
using Xunit;

namespace Haworks.BuildingBlocks.Tests.Authentication;

public sealed class UserIdentityForwardingHandlerTests
{
    private readonly Mock<IHttpContextAccessor> _accessor = new();
    private readonly UserIdentityForwardingHandler _sut;
    private readonly Mock<HttpMessageHandler> _innerHandler = new();

    public UserIdentityForwardingHandlerTests()
    {
        _sut = new UserIdentityForwardingHandler(_accessor.Object)
        {
            InnerHandler = _innerHandler.Object
        };
    }

    [Theory]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public async Task Forwards_X_User_Id_when_HttpContext_has_authenticated_user(string claimType)
    {
        // Arrange
        var userId = "user-123";
        var claims = new[] { new Claim(claimType, userId) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        _accessor.Setup(a => a.HttpContext).Returns(httpContext);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://backend/api/resource");
        
        _innerHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        // Act
        var invoker = new HttpMessageInvoker(_sut);
        await invoker.SendAsync(request, CancellationToken.None);

        // Assert
        request.Headers.Contains(UserIdentityForwardingHandler.HeaderName).Should().BeTrue();
        request.Headers.GetValues(UserIdentityForwardingHandler.HeaderName).First().Should().Be(userId);
    }

    [Fact]
    public async Task Does_not_forward_when_HttpContext_is_null()
    {
        // Arrange
        _accessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);
        var request = new HttpRequestMessage(HttpMethod.Get, "http://backend/api/resource");

        _innerHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        // Act
        var invoker = new HttpMessageInvoker(_sut);
        await invoker.SendAsync(request, CancellationToken.None);

        // Assert
        request.Headers.Contains(UserIdentityForwardingHandler.HeaderName).Should().BeFalse();
    }

    [Fact]
    public async Task Does_not_overwrite_existing_X_User_Id_header()
    {
        // Arrange
        var userId = "user-123";
        var existingUserId = "existing-user";
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        _accessor.Setup(a => a.HttpContext).Returns(httpContext);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://backend/api/resource");
        request.Headers.Add(UserIdentityForwardingHandler.HeaderName, existingUserId);

        _innerHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        // Act
        var invoker = new HttpMessageInvoker(_sut);
        await invoker.SendAsync(request, CancellationToken.None);

        // Assert
        request.Headers.GetValues(UserIdentityForwardingHandler.HeaderName).First().Should().Be(existingUserId);
    }

    [Fact]
    public async Task Reads_from_NameIdentifier_then_falls_back_to_sub()
    {
        // Arrange
        var userIdNameId = "user-nameid";
        var userIdSub = "user-sub";
        var claims = new[] 
        { 
            new Claim(ClaimTypes.NameIdentifier, userIdNameId),
            new Claim("sub", userIdSub)
        };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        _accessor.Setup(a => a.HttpContext).Returns(httpContext);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://backend/api/resource");
        
        _innerHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        // Act
        var invoker = new HttpMessageInvoker(_sut);
        await invoker.SendAsync(request, CancellationToken.None);

        // Assert
        request.Headers.GetValues(UserIdentityForwardingHandler.HeaderName).First().Should().Be(userIdNameId);
    }
}
