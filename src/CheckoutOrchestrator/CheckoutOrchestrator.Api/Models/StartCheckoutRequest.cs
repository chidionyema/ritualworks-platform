using Haworks.Contracts.Checkout;

namespace Haworks.CheckoutOrchestrator.Api.Models;

public sealed record StartCheckoutRequest(
    Guid SagaId,
    Guid OrderId,
    string UserId,
    string CustomerEmail,
    decimal TotalAmount,
    string IdempotencyKey,
    IReadOnlyList<CheckoutItemData> Items);
