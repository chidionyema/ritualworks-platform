namespace Haworks.Catalog.Domain;

public class ProductSpecification
{
    protected ProductSpecification() { }

    private ProductSpecification(Guid productId, string name, string value, int displayOrder)
    {
        Id = Guid.NewGuid();
        ProductId = productId;
        Name = name;
        Value = value;
        DisplayOrder = displayOrder;
    }

    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public int DisplayOrder { get; private set; }
    public Product? Product { get; private set; }

    public static ProductSpecification Create(Guid productId, string name, string value, int displayOrder = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ProductSpecification(productId, name, value, displayOrder);
    }

    public void Update(string name, string value, int displayOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Value = value;
        DisplayOrder = displayOrder;
    }
}
