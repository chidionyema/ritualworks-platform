using FluentAssertions;
using FluentValidation;
using Haworks.FeatureFlags.Api.Application;
using Haworks.FeatureFlags.Api.Domain;
using Haworks.FeatureFlags.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Haworks.FeatureFlags.Unit;

public sealed class FeatureFlagHardeningTests
{
    [Fact]
    public void Concurrency_token_is_configured_as_xmin()
    {
        var options = new DbContextOptionsBuilder<FeatureFlagsDbContext>()
            .UseNpgsql("Host=localhost") // not actually connecting
            .Options;

        using var context = new FeatureFlagsDbContext(options);
        var model = context.Model;
        var entity = model.FindEntityType(typeof(FeatureFlag))!;

        var rowVersion = entity.FindProperty(nameof(FeatureFlag.RowVersion))!;
        rowVersion.IsConcurrencyToken.Should().BeTrue();
        rowVersion.GetColumnName().Should().Be("xmin");
    }

    [Fact]
    public async Task Audit_fields_are_stamped_on_add()
    {
        var options = new DbContextOptionsBuilder<FeatureFlagsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new FeatureFlagsDbContext(options);

        var flag = new FeatureFlag
        {
            Id = Guid.NewGuid(),
            Name = "audit-test",
            IsEnabled = false,
            Description = "test"
        };

        context.FeatureFlags.Add(flag);
        await context.SaveChangesAsync();

        flag.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        flag.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Audit_fields_updated_on_modify()
    {
        var options = new DbContextOptionsBuilder<FeatureFlagsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new FeatureFlagsDbContext(options);

        var flag = new FeatureFlag
        {
            Id = Guid.NewGuid(),
            Name = "audit-modify",
            IsEnabled = false,
            Description = "initial"
        };
        context.FeatureFlags.Add(flag);
        await context.SaveChangesAsync();

        var originalCreatedAt = flag.CreatedAt;

        // Simulate delay
        flag.Description = "modified";
        context.Entry(flag).State = EntityState.Modified;
        await context.SaveChangesAsync();

        flag.UpdatedAt.Should().BeOnOrAfter(originalCreatedAt);
    }

    [Fact]
    public async Task Delete_enabled_flag_returns_failure()
    {
        var options = new DbContextOptionsBuilder<FeatureFlagsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new FeatureFlagsDbContext(options);

        var flag = new FeatureFlag
        {
            Id = Guid.NewGuid(),
            Name = "enabled-flag",
            IsEnabled = true,
            Description = "active"
        };
        context.FeatureFlags.Add(flag);
        await context.SaveChangesAsync();

        var handler = new DeleteFlagHandler(context);
        var result = await handler.Handle(new DeleteFlagCommand("enabled-flag"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Flag.StillEnabled");
    }

    [Fact]
    public async Task Delete_disabled_flag_succeeds()
    {
        var options = new DbContextOptionsBuilder<FeatureFlagsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new FeatureFlagsDbContext(options);

        var flag = new FeatureFlag
        {
            Id = Guid.NewGuid(),
            Name = "disabled-flag",
            IsEnabled = false,
            Description = "inactive"
        };
        context.FeatureFlags.Add(flag);
        await context.SaveChangesAsync();

        var handler = new DeleteFlagHandler(context);
        var result = await handler.Handle(new DeleteFlagCommand("disabled-flag"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        context.FeatureFlags.Any(f => f.Name == "disabled-flag").Should().BeFalse();
    }

    [Fact]
    public void UpdateFlagValidator_rejects_more_than_20_rules()
    {
        var validator = new UpdateFlagValidator();
        var rules = Enumerable.Range(0, 21)
            .Select(_ => new FeatureFlagRule { Id = Guid.NewGuid() })
            .ToList();

        var command = new UpdateFlagCommand("test-flag", true, "desc", rules);
        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("20 rules"));
    }

    [Fact]
    public void UpdateFlagValidator_accepts_20_rules()
    {
        var validator = new UpdateFlagValidator();
        var rules = Enumerable.Range(0, 20)
            .Select(_ => new FeatureFlagRule { Id = Guid.NewGuid() })
            .ToList();

        var command = new UpdateFlagCommand("test-flag", true, "desc", rules);
        var result = validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }
}
