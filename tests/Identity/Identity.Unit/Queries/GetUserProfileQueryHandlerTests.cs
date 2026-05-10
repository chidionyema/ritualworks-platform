using Haworks.Identity.Application.Queries.Users;
using Haworks.Identity.Application.DTOs;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Microsoft.AspNetCore.Identity;
using Moq;
using Xunit.Abstractions;
using Xunit;

namespace Haworks.Identity.UnitTests.Queries.Users;

public class GetUserProfileQueryHandlerTests : TestBase
{
    private readonly Mock<UserManager<User>> _userManagerMock;
    private readonly Mock<IUserProfileRepository> _userProfileRepositoryMock;
    private readonly GetUserProfileQueryHandler _handler;

    public GetUserProfileQueryHandlerTests(ITestOutputHelper output) : base(output)
    {
        var storeMock = new Mock<IUserStore<User>>();
        _userManagerMock = new Mock<UserManager<User>>(
            storeMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        _userProfileRepositoryMock = new Mock<IUserProfileRepository>();
        _handler = new GetUserProfileQueryHandler(_userManagerMock.Object, _userProfileRepositoryMock.Object);
    }

    [Fact]
    public async Task Handle_WithExistingProfile_ReturnsProfileDto()
    {
        var userId = Guid.NewGuid().ToString();
        var profile = UserProfile.Create(userId);
        profile.UpdatePersonalInfo("John", "Doe");

        _userProfileRepositoryMock
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        var result = await _handler.Handle(new GetUserProfileQuery(userId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("John", result.Value.FirstName);
    }
}
