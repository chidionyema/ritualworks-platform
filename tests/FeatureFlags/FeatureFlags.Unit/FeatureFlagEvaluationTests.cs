using FluentAssertions;
using Haworks.FeatureFlags.Api.Domain;
using Xunit;

namespace Haworks.FeatureFlags.Unit;

public sealed class FeatureFlagEvaluationTests
{
    [Fact]
    public void New_flag_defaults_to_disabled()
    {
        var flag = new FeatureFlag { Id = Guid.NewGuid(), Name = "test-flag" };

        flag.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Flag_with_percentage_rule_at_zero_is_effectively_disabled()
    {
        var flag = new FeatureFlag
        {
            Id = Guid.NewGuid(),
            Name = "rollout",
            IsEnabled = true,
            Rules = [new FeatureFlagRule { Id = Guid.NewGuid(), PercentageRollout = 0 }]
        };

        flag.Rules.Should().ContainSingle()
            .Which.PercentageRollout.Should().Be(0);
    }

    [Fact]
    public void Flag_rule_scopes_to_specific_user()
    {
        var userId = "user-123";
        var rule = new FeatureFlagRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Region = null,
            PercentageRollout = null
        };

        rule.UserId.Should().Be(userId);
        rule.Region.Should().BeNull();
    }
}
