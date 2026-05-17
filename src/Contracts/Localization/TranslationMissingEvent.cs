namespace Haworks.Contracts.Localization;

public sealed record TranslationMissingEvent : DomainEvent
{
    public required string Key { get; init; }
    public required string Locale { get; init; }
}
