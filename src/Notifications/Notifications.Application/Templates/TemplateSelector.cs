using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Templates;

/// <summary>
/// Default <see cref="ITemplateSelector"/>. Resolves the active template
/// version for a (templateId, locale, channel) triple, applying the
/// locale-fallback rule from <c>docs/architecture/notification-service.md §7</c>:
/// try the exact locale first, then the wildcard <c>"*"</c> fallback.
/// Returns <c>null</c> (typed as the non-nullable interface return for
/// L0 ABI compatibility) when no active template exists for either locale.
/// </summary>
public sealed class TemplateSelector : ITemplateSelector
{
    private const string WildcardLocale = "*";

    private readonly ITemplateRepository _repository;

    public TemplateSelector(ITemplateRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<NotificationTemplate> SelectAsync(
        string templateId,
        string locale,
        NotificationChannel channel,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentNullException.ThrowIfNull(locale);

        var versions = await _repository.GetVersionsAsync(templateId, ct).ConfigureAwait(false);
        var materialised = versions as IList<NotificationTemplate> ?? versions.ToList();

        var channelString = channel.ToString();

        var exact = PickActive(materialised, channelString, locale);
        if (exact is not null) return exact;

        if (!string.Equals(locale, WildcardLocale, StringComparison.Ordinal))
        {
            var fallback = PickActive(materialised, channelString, WildcardLocale);
            if (fallback is not null) return fallback;
        }

        // L0 contract is non-nullable Task<NotificationTemplate>; the
        // documented "no template" case maps to null at the call site.
        return null!;
    }

    private static NotificationTemplate? PickActive(
        IEnumerable<NotificationTemplate> versions,
        string channel,
        string locale)
    {
        return versions
            .Where(t => t.IsActive
                        && string.Equals(t.Channel, channel, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.Locale, locale, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Version)
            .FirstOrDefault();
    }
}
