namespace Haworks.Contracts.FeatureFlags;

public sealed record FeatureFlagUpdated : DomainEvent
{
    public string FlagName { get; init; } = "";
    public bool IsEnabled { get; init; }
}
