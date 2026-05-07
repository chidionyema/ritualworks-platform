using Haworks.Payments.Application.Interfaces;

namespace Haworks.Payments.Unit.Builders;

public class CheckoutSessionRequestBuilder
{
    private List<LineItem> _items = new();
    private string _customerEmail = "test@example.com";
    private string _successUrl = "https://example.com/success";
    private string _cancelUrl = "https://example.com/cancel";
    private string _idempotencyKey = Guid.NewGuid().ToString();
    private Guid _orderId = Guid.NewGuid();

    public static CheckoutSessionRequestBuilder Create() => new();

    public CheckoutSessionRequestBuilder WithItem(string name, long unitAmountCents, int quantity = 1)
    {
        _items.Add(new LineItem
        {
            Name = name,
            UnitAmountCents = unitAmountCents,
            Quantity = quantity,
            Currency = "USD"
        });
        return this;
    }

    public CheckoutSessionRequestBuilder WithEmail(string email)
    {
        _customerEmail = email;
        return this;
    }

    public CheckoutSessionRequestBuilder WithOrderId(Guid orderId)
    {
        _orderId = orderId;
        return this;
    }

    public CheckoutSessionRequestBuilder WithIdempotencyKey(string key)
    {
        _idempotencyKey = key;
        return this;
    }

    public CreateCheckoutSessionRequest Build() => new()
    {
        LineItems = _items,
        CustomerEmail = _customerEmail,
        SuccessUrl = _successUrl,
        CancelUrl = _cancelUrl,
        IdempotencyKey = _idempotencyKey,
        OrderId = _orderId,
        Currency = "USD"
    };
}
