using FluentAssertions;
using Haworks.Identity.Application.Queries.Users;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Microsoft.AspNetCore.Identity;
using Moq;
using Xunit;

namespace Haworks.Identity.Unit.Queries.Users;

public class GetUserProfileQueryHandlerTests
{
    private readonly Mock<UserManager<User>> _userManagerMock;
    private readonly Mock<IUserProfileRepository> _profileRepositoryMock;
    private readonly GetUserProfileQueryHandler _handler;

    public GetUserProfileQueryHandlerTests()
    {
        var store = new Mock<IUserStore<User>>();
        _userManagerMock = new Mock<UserManager<User>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        _profileRepositoryMock = new Mock<IUserProfileRepository>();
        _handler = new GetUserProfileQueryHandler(_userManagerMock.Object, _profileRepositoryMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidUserAndProfile_ReturnsFullProfile()
    {
        var userId = "user-123";
        var user = new User { Id = userId, UserName = "testuser", Email = "test@example.com" };
        var profile = UserProfile.Create(userId);
        profile.UpdatePersonalInfo("John", "Doe", "+1234567890");

        _userManagerMock.Setup(m => m.FindByIdAsync(userId)).ReturnsAsync(user);
        _profileRepositoryMock.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        var result = await _handler.Handle(new GetUserProfileQuery(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FirstName.Should().Be("John");
        result.Value.Email.Should().Be("test@example.com");
    }

    [Fact]
    public async Task Handle_WithEmptyUserId_ReturnsFailure()
    {
        var result = await _handler.Handle(new GetUserProfileQuery(""), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
    }
}
