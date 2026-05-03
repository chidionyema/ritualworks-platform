namespace Haworks.Catalog.Domain;

/// <summary>
/// Top-level product grouping. Each Product belongs to exactly one Category.
/// </summary>
public class Category : AuditableEntity
{
    private readonly List<Product> _products = new();

    /// <summary>EF Core materialization constructor.</summary>
    protected Category() : base() { }

    private Category(string name, string? description) : base()
    {
        Name = name;
        Description = description;
    }

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    public IReadOnlyCollection<Product> Products => _products.AsReadOnly();

    public static Category Create(string name, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Category(name, description);
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        LastModifiedDate = DateTime.UtcNow;
    }
}
