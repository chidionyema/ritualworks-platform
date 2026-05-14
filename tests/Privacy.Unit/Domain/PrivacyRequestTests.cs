using FluentAssertions;
using Haworks.Privacy.Domain.Aggregates;
using Haworks.Privacy.Domain.Enums;
using Xunit;

namespace Haworks.Privacy.Unit.Domain;

public class PrivacyRequestTests
{
    [Fact]
    public void Create_Should_Set_Initial_Status_To_Pending()
    {
        var userId = Guid.NewGuid();
        var request = PrivacyRequest.Create(userId, PrivacyRequestType.Erasure);

        request.Status.Should().Be(PrivacyRequestStatus.Pending);
        request.UserId.Should().Be(userId);
    }

    [Fact]
    public void Start_Should_Transition_To_InProgress()
    {
        var request = PrivacyRequest.Create(Guid.NewGuid(), PrivacyRequestType.Export);
        request.Start();

        request.Status.Should().Be(PrivacyRequestStatus.InProgress);
    }

    [Fact]
    public void Complete_Should_Set_Status_And_Timestamp()
    {
        var request = PrivacyRequest.Create(Guid.NewGuid(), PrivacyRequestType.Erasure);
        request.Complete();

        request.Status.Should().Be(PrivacyRequestStatus.Completed);
        request.CompletedAt.Should().NotBeNull();
    }
}
