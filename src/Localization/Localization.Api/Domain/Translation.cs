namespace Haworks.Localization.Api.Domain;

public class Translation
{
    public Guid Id { get; private set; }
    public string Key { get; private set; } = default!;

    // Dictionary<Locale, Value>
    public Dictionary<string, string> Values { get; private set; } = new();

    public string? UpdatedBy { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    private Translation() { }

    public Translation(string key, Dictionary<string, string> values)
    {
        Id = Guid.NewGuid();
        Key = key;
        Values = values;
    }

    public void UpdateValue(string locale, string value, string? updatedBy = null)
    {
        Values[locale] = value;
        UpdatedBy = updatedBy;
        UpdatedAt = DateTime.UtcNow;
    }
}
