namespace Haworks.FeatureFlags.Api.Domain;

public class FeatureFlag
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public List<FeatureFlagRule> Rules { get; set; } = new();
}

public class FeatureFlagRule
{
    public Guid Id { get; set; }
    public Guid FeatureFlagId { get; set; }
    public string? UserId { get; set; }
    public string? Region { get; set; }
    public int? PercentageRollout { get; set; }
}
