using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Catalog.Domain;

public class ProductMetadata : AuditableEntity
{
    protected ProductMetadata() : base() { }

    private ProductMetadata(Guid productId, string keyName, string keyValue) : base()
    {
        ProductId = productId;
        KeyName = keyName;
        KeyValue = keyValue;
    }

    public Guid ProductId { get; private set; }
    public string KeyName { get; private set; } = string.Empty;
    public string KeyValue { get; private set; } = string.Empty;
    public Product? Product { get; private set; }

    public static ProductMetadata Create(Guid productId, string keyName, string keyValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        return new ProductMetadata(productId, keyName, keyValue);
    }

    public void UpdateValue(string newValue)
    {
        KeyValue = newValue;
    }
}
