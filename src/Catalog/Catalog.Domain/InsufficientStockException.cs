namespace Haworks.Catalog.Domain;

/// <summary>
/// Thrown by <see cref="Interfaces.IProductRepository.CreateReservationAsync"/>
/// when the atomic stock-decrement transaction would drive any product's
/// <c>StockQuantity</c> negative. The caller (B2's sync reservation
/// endpoint) translates this to HTTP 409 Conflict.
/// </summary>
public sealed class InsufficientStockException : Exception
{
    public InsufficientStockException(Guid productId, int requested, int available)
        : base($"Insufficient stock for product {productId}: requested {requested}, available {available}.")
    {
        ProductId = productId;
        Requested = requested;
        Available = available;
    }

    public Guid ProductId { get; }
    public int Requested { get; }
    public int Available { get; }
}
