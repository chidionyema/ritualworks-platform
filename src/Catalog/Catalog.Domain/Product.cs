namespace Haworks.Catalog.Domain;

/// <summary>
/// A sellable item in the catalog. Owns its own stock count — stock
/// reservation/release is a method on Product, NOT a separate service,
/// because the optimistic concurrency check (RowVersion + WHERE
/// stock >= qty) must be atomic with the read.
///
/// Per ADR-0009 (DB-per-service), Product carries no cross-context
/// references — no UserProfile, no ContentEntity, no Order. Other
/// services maintain their own product snapshots via the
/// <c>ProductUpdated</c> event (when wired in Phase 2c).
/// </summary>
public class Product : AuditableEntity
{
    private readonly List<ProductReview> _reviews = new();
    private readonly List<ProductMetadata> _metadata = new();
    private readonly List<ProductSpecification> _specifications = new();

    /// <summary>EF Core materialization constructor.</summary>
    protected Product() : base() { }

    private Product(string name, string description, decimal unitPrice, Guid categoryId)
        : base()
    {
        Name = name;
        Description = description;
        UnitPrice = unitPrice;
        CategoryId = categoryId;
        IsListed = false;
        IsInStock = false;
        StockQuantity = 0;
    }

    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public decimal UnitPrice { get; private set; }
    public int StockQuantity { get; private set; }
    public bool IsInStock { get; private set; }
    public bool IsListed { get; private set; }

    public Guid CategoryId { get; private set; }
    public Category? Category { get; private set; }

    public IReadOnlyCollection<ProductReview> Reviews => _reviews.AsReadOnly();
    public IReadOnlyCollection<ProductMetadata> Metadata => _metadata.AsReadOnly();
    public IReadOnlyCollection<ProductSpecification> Specifications => _specifications.AsReadOnly();

    public static Product Create(string name, string description, decimal unitPrice, Guid categoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (unitPrice < 0) throw new ArgumentException("Price cannot be negative", nameof(unitPrice));
        return new Product(name, description, unitPrice, categoryId);
    }

    public void UpdateBasicInfo(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description;
        LastModifiedDate = DateTime.UtcNow;
    }

    public void UpdatePricing(decimal unitPrice)
    {
        if (unitPrice < 0) throw new ArgumentException("Price cannot be negative", nameof(unitPrice));
        UnitPrice = unitPrice;
        LastModifiedDate = DateTime.UtcNow;
    }

    public void RestockTo(int quantity)
    {
        if (quantity < 0) throw new ArgumentException("Stock cannot be negative", nameof(quantity));
        StockQuantity = quantity;
        IsInStock = quantity > 0;
        LastModifiedDate = DateTime.UtcNow;
    }

    /// <summary>
    /// Tries to reserve <paramref name="quantity"/> units atomically.
    /// Returns false if insufficient stock; true on success (caller must
    /// SaveChangesAsync to persist + emit StockReservedEvent via outbox).
    ///
    /// EF + RowVersion concurrency control catches concurrent reservers:
    /// the second SaveChanges throws DbUpdateConcurrencyException, the
    /// caller retries, and the second attempt sees the decremented count.
    /// </summary>
    public bool ReserveStock(int quantity)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive", nameof(quantity));
        if (StockQuantity < quantity) return false;

        StockQuantity -= quantity;
        IsInStock = StockQuantity > 0;
        LastModifiedDate = DateTime.UtcNow;
        return true;
    }

    /// <summary>Returns previously-reserved stock to inventory (compensation path).</summary>
    public void ReleaseStock(int quantity)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive", nameof(quantity));
        StockQuantity += quantity;
        IsInStock = true;
        LastModifiedDate = DateTime.UtcNow;
    }

    public void List()    { IsListed = true;  LastModifiedDate = DateTime.UtcNow; }
    public void Unlist()  { IsListed = false; LastModifiedDate = DateTime.UtcNow; }

    public void AddMetadata(string key, string value)
    {
        var existing = _metadata.FirstOrDefault(m => m.KeyName == key);
        if (existing != null)
        {
            existing.UpdateValue(value);
        }
        else
        {
            _metadata.Add(ProductMetadata.Create(Id, key, value));
        }
        LastModifiedDate = DateTime.UtcNow;
    }

    public void AddSpecification(string name, string value, int displayOrder = 0)
    {
        _specifications.Add(ProductSpecification.Create(Id, name, value, displayOrder));
        LastModifiedDate = DateTime.UtcNow;
    }
}
