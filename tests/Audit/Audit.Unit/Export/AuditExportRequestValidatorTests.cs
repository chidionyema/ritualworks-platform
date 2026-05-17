using FluentAssertions;
using Haworks.Audit.Application.Export;
using Xunit;

namespace Haworks.Audit.Unit.Export;

public class AuditExportRequestValidatorTests
{
    private readonly AuditExportRequestValidator _sut = new();

    [Fact]
    public async Task Valid_request_passes()
    {
        var request = new AuditExportRequest(
            EntityId: null,
            EntityType: null,
            EventType: null,
            From: DateTimeOffset.UtcNow.AddDays(-30),
            To: DateTimeOffset.UtcNow);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_date_range_exceeding_90_days()
    {
        var request = new AuditExportRequest(
            EntityId: null,
            EntityType: null,
            EventType: null,
            From: DateTimeOffset.UtcNow.AddDays(-91),
            To: DateTimeOffset.UtcNow);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains($"{AuditExportRequestValidator.MaxDateRangeDays} days"));
    }

    [Fact]
    public async Task Rejects_end_date_before_start_date()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new AuditExportRequest(
            EntityId: null,
            EntityType: null,
            EventType: null,
            From: now,
            To: now.AddDays(-1));

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("End date must be after start date"));
    }

    [Fact]
    public async Task Rejects_default_from_date()
    {
        var request = new AuditExportRequest(
            EntityId: null,
            EntityType: null,
            EventType: null,
            From: default,
            To: DateTimeOffset.UtcNow);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "From");
    }

    [Fact]
    public async Task Rejects_default_to_date()
    {
        var request = new AuditExportRequest(
            EntityId: null,
            EntityType: null,
            EventType: null,
            From: DateTimeOffset.UtcNow.AddDays(-1),
            To: default);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "To");
    }

    [Fact]
    public async Task Exactly_90_days_passes()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new AuditExportRequest(
            EntityId: null,
            EntityType: null,
            EventType: null,
            From: now.AddDays(-90),
            To: now);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }
}
