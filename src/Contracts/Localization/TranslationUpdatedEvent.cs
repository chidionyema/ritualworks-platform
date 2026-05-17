namespace Haworks.Contracts.Localization;

public sealed record TranslationUpdatedEvent : DomainEvent
{
    public required Guid TranslationId { get; init; }
    public required string Key { get; init; }
    public required string Locale { get; init; }
    public required string Value { get; init; }
    public required string UpdatedBy { get; init; }
}
