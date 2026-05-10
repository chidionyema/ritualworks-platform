using FluentAssertions;
using Haworks.Content.Domain.Entities;
using Haworks.Content.UnitTests.Helpers;
using Xunit;

namespace Haworks.Content.UnitTests.Domain;

public sealed class ContentEntityTests
{
    [Fact]
    public void CreatePending_yields_a_Pending_entity_with_no_validation_metadata()
    {
        var entity = DomainTestHelpers.CreatePendingContentEntity();

        entity.Status.Should().Be(ContentStatus.Pending);
        entity.ValidatedAt.Should().BeNull();
        entity.Sha256Checksum.Should().BeNull();
        entity.ETag.Should().BeEmpty();
    }

    [Fact]
    public void CreatePending_for_multipart_requires_an_S3_upload_id()
    {
        var act = () => DomainTestHelpers.CreatePendingContentEntity(
            uploadKind: UploadKind.Multipart, s3UploadId: null);

        act.Should().Throw<ArgumentException>().WithParameterName("s3UploadId");
    }

    [Fact]
    public void MarkValidating_only_succeeds_from_Pending()
    {
        var entity = DomainTestHelpers.CreatePendingContentEntity();

        entity.MarkValidating();
        entity.Status.Should().Be(ContentStatus.Validating);

        var act = () => entity.MarkValidating();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkAvailable_records_etag_checksum_and_validatedAt()
    {
        var entity = DomainTestHelpers.CreatePendingContentEntity();
        entity.MarkValidating();

        var now = DateTime.UtcNow;
        entity.MarkAvailable(
            etag: "abc",
            sha256Checksum: "deadbeef",
            actualSize: 2048,
            url: "https://signed",
            utcNow: now);

        entity.Status.Should().Be(ContentStatus.Available);
        entity.ETag.Should().Be("abc");
        entity.Sha256Checksum.Should().Be("deadbeef");
        entity.FileSize.Should().Be(2048);
        entity.ValidatedAt.Should().Be(now);
        entity.Url.Should().Be("https://signed");
    }

    [Fact]
    public void Quarantine_records_reason_and_blocks_further_transitions()
    {
        var entity = DomainTestHelpers.CreatePendingContentEntity();
        entity.MarkValidating();

        entity.Quarantine("virus: EICAR");

        entity.Status.Should().Be(ContentStatus.Quarantined);
        entity.QuarantineReason.Should().Be("virus: EICAR");

        var reMark = () => entity.MarkAvailable("e", "c", 1, "u", DateTime.UtcNow);
        reMark.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Fail_from_Pending_records_reason()
    {
        var entity = DomainTestHelpers.CreatePendingContentEntity();

        entity.Fail("aborted");

        entity.Status.Should().Be(ContentStatus.Failed);
        entity.FailureReason.Should().Be("aborted");
    }

    [Fact]
    public void SoftDelete_is_idempotent()
    {
        var entity = DomainTestHelpers.CreatePendingContentEntity();
        entity.MarkValidating();
        entity.MarkAvailable("e", "c", 1, "u", DateTime.UtcNow);

        entity.SoftDelete();
        entity.SoftDelete();

        entity.Status.Should().Be(ContentStatus.Deleted);
    }
}
