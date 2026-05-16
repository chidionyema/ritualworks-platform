using FluentAssertions;
using Haworks.Privacy.Domain.Aggregates;
using Haworks.Privacy.Domain.Enums;
using Xunit;

namespace Haworks.Privacy.Unit.Domain;

public sealed class PrivacyRequestTests
{
    [Fact]
    public void Create_sets_status_to_Pending()
    {
        var request = PrivacyRequest.Create(Guid.NewGuid(), PrivacyRequestType.Erasure);

        request.Status.Should().Be(PrivacyRequestStatus.Pending);
        request.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Start_transitions_to_InProgress()
    {
        var request = PrivacyRequest.Create(Guid.NewGuid(), PrivacyRequestType.Erasure);

        request.Start();

        request.Status.Should().Be(PrivacyRequestStatus.InProgress);
    }

    [Fact]
    public void Complete_sets_status_and_timestamp()
    {
        var request = PrivacyRequest.Create(Guid.NewGuid(), PrivacyRequestType.Export);
        var contentId = Guid.NewGuid();
        request.Start();

        request.Complete(contentId);

        request.Status.Should().Be(PrivacyRequestStatus.Completed);
        request.ContentId.Should().Be(contentId);
        request.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Fail_sets_status_to_Failed()
    {
        var request = PrivacyRequest.Create(Guid.NewGuid(), PrivacyRequestType.Erasure);
        request.Start();

        request.Fail();

        request.Status.Should().Be(PrivacyRequestStatus.Failed);
    }

    [Fact]
    public void Complete_on_already_completed_throws()
    {
        var request = PrivacyRequest.Create(Guid.NewGuid(), PrivacyRequestType.Export);
        request.Start();
        request.Complete();

        var act = () => request.Complete();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already-completed*");
    }

    [Fact]
    public void Fail_on_already_completed_throws()
    {
        var request = PrivacyRequest.Create(Guid.NewGuid(), PrivacyRequestType.Erasure);
        request.Start();
        request.Complete();

        var act = () => request.Fail();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*terminal state*");
    }
}
