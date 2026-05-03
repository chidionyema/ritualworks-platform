namespace Haworks.BffWeb.Api;

/// <summary>
/// Named-HttpClient keys for the typed clients bff-web uses to talk to
/// the backend services. The configured BaseAddress comes from Aspire's
/// service-discovery (`https+http://identity-svc` etc.) — matches the
/// `WithReference(identity)` calls in the AppHost.
/// </summary>
public static class BackendClients
{
    public const string Identity = "identity-svc";
    public const string Catalog = "catalog-svc";
    public const string Orders = "orders-svc";
    public const string Payments = "payments-svc";
    public const string Checkout = "checkout-svc";
}
