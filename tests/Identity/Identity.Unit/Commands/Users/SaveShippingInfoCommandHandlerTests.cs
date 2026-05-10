using Haworks.Identity.Application.Commands.Users;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Moq;
using Xunit.Abstractions;
using Xunit;

namespace Haworks.Identity.UnitTests.Commands.Users;

public class SaveShippingInfoCommandHandlerTests : TestBase
{
    private readonly Mock<IUserProfileRepository> _userProfileRepositoryMock;
    private readonly SaveShippingInfoCommandHandler _handler;

    public SaveShippingInfoCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        _userProfileRepositoryMock = new Mock<IUserProfileRepository>();
        _handler = new SaveShippingInfoCommandHandler(_userProfileRepositoryMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidRequest_UpsertsProfile()
    {
        var userId = Guid.NewGuid().ToString();
        var command = new SaveShippingInfoCommand(
            userId, "John", "Doe", "123 St", "NY", "NY", "10001", "US", null);

        _userProfileRepositoryMock
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserProfile?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        _userProfileRepositoryMock.Verify(x => x.AddAsync(It.IsAny<UserProfile>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
