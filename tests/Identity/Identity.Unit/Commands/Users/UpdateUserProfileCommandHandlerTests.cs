using Haworks.Identity.Application.Commands.Users;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Haworks.Identity.UnitTests.Helpers;
using Microsoft.AspNetCore.Identity;
using Moq;
using Xunit.Abstractions;
using Xunit;

namespace Haworks.Identity.UnitTests.Commands.Users;

public class UpdateUserProfileCommandHandlerTests : TestBase
{
    private readonly Mock<UserManager<User>> _userManagerMock;
    private readonly Mock<IUserProfileRepository> _userProfileRepositoryMock;
    private readonly UpdateUserProfileCommandHandler _handler;

    public UpdateUserProfileCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        var storeMock = new Mock<IUserStore<User>>();
        _userManagerMock = new Mock<UserManager<User>>(
            storeMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _userProfileRepositoryMock = new Mock<IUserProfileRepository>();

        _handler = new UpdateUserProfileCommandHandler(
            _userManagerMock.Object,
            _userProfileRepositoryMock.Object);
    }

    [Fact]
    public async Task Handle_WithEmptyUserId_ReturnsValidationError()
    {
        var command = new UpdateUserProfileCommand(
            UserId: string.Empty,
            FirstName: "John",
            LastName: "Doe",
            Phone: null,
            Address: null,
            City: null,
            State: null,
            PostalCode: null,
            Country: null,
            Bio: null,
            Website: null);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Users.MissingUserId", result.Error.Code);
    }

    [Fact]
    public async Task Handle_WithExistingProfile_UpdatesProfile()
    {
        var userId = Guid.NewGuid().ToString();
        var user = new User { Id = userId, UserName = "testuser" };
        var existingProfile = UserProfile.Create(userId);

        _userManagerMock
            .Setup(x => x.FindByIdAsync(userId))
            .ReturnsAsync(user);

        _userProfileRepositoryMock
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingProfile);

        var command = new UpdateUserProfileCommand(
            UserId: userId,
            FirstName: "John",
            LastName: "Doe",
            Phone: "+1234567890",
            Address: "123 Main St",
            City: "New York",
            State: "NY",
            PostalCode: "10001",
            Country: "USA",
            Bio: "A brief bio",
            Website: "https://example.com");

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("John", existingProfile.FirstName);
        Assert.Equal("Doe", existingProfile.LastName);
    }
}
