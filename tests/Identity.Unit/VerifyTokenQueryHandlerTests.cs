using System.Security.Claims;
using FluentAssertions;
using Haworks.BuildingBlocks.Common;
using Haworks.Identity.Application;
using Haworks.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Haworks.Identity.Unit;

/// <summary>
/// Unit tests for VerifyTokenQueryHandler. The handler trusts that signature
/// + revocation has already been validated by the JwtBearer middleware (with
/// our OnTokenValidated revocation hook). Its job is to extract the userId
/// from claims and confirm the user still exists in the DB — defensive
/// against a JWT issued for a since-deleted account.
/// </summary>
public sealed class VerifyTokenQueryHandlerTests
{
    [Fact]
    public async Task Handle_returns_authenticated_dto_for_existing_user()
    {
        var userManager = MockUserManager(existingUser: new User { Id = "user-123", UserName = "alice" });
        var sut = new VerifyTokenQueryHandler(userManager.Object, NullLogger<VerifyTokenQueryHandler>.Instance);

        var principal = AuthedPrincipal(userId: "user-123", userName: "alice");
        var result = await sut.Handle(new VerifyTokenQuery(principal), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsAuthenticated.Should().BeTrue();
        result.Value.UserId.Should().Be("user-123");
        result.Value.UserName.Should().Be("alice");
    }

    [Fact]
    public async Task Handle_returns_failure_when_user_no_longer_exists_in_db()
    {
        // Token was issued for a user that has since been deleted.
        // Signature is still valid (signed by us, not yet expired) but the
        // user account is gone — must NOT report authenticated.
        var userManager = MockUserManager(existingUser: null);
        var sut = new VerifyTokenQueryHandler(userManager.Object, NullLogger<VerifyTokenQueryHandler>.Instance);

        var principal = AuthedPrincipal(userId: "ghost-user", userName: "ghost");
        var result = await sut.Handle(new VerifyTokenQuery(principal), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Auth.UserDeleted");
    }

    [Fact]
    public async Task Handle_with_principal_lacking_userId_falls_back_to_name_lookup()
    {
        // Principal has Identity.Name but no NameIdentifier/Sub claim.
        // Handler should look up the user by name as a fallback.
        var existing = new User { Id = "user-789", UserName = "bob" };
        var userManager = MockUserManager(existingUser: existing);
        userManager.Setup(m => m.FindByNameAsync("bob")).ReturnsAsync(existing);

        var sut = new VerifyTokenQueryHandler(userManager.Object, NullLogger<VerifyTokenQueryHandler>.Instance);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims: new[] { new Claim(ClaimTypes.Name, "bob") },
            authenticationType: "Bearer"));

        var result = await sut.Handle(new VerifyTokenQuery(principal), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be("user-789");
    }

    private static Mock<UserManager<User>> MockUserManager(User? existingUser)
    {
        var store = new Mock<IUserStore<User>>();
        var mgr = new Mock<UserManager<User>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.Setup(m => m.FindByIdAsync(It.IsAny<string>())).ReturnsAsync(existingUser);
        return mgr;
    }

    private static ClaimsPrincipal AuthedPrincipal(string userId, string userName) =>
        new(new ClaimsIdentity(
            claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, userName),
            },
            authenticationType: "Bearer"));
}
