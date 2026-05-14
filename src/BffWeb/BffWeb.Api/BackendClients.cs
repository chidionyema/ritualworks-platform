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
    public const string Search = "search-svc";
    public const string Location = "location-svc";
    public const string Webhooks = "webhooks-svc";
    public const string Payouts = "payouts-svc";
    public const string Scheduler = "scheduler-svc";
    public const string Privacy = "privacy-svc";
    public const string Merchant = "merchant-svc";
    public const string Notifications = "notifications-svc";
    public const string Content = "content-svc";
    public const string Audit = "audit-svc";

    /// <summary>
    /// Separate typed-client identity for the T2.3 circuit-breaker demo
    /// against catalog-svc. Same BaseAddress as <see cref="Catalog"/>, but
    /// the registration adds a Polly circuit breaker (2 failures -> open
    /// for 6s) so the demo can demonstrate fail-fast behaviour without
    /// affecting the unrelated catalog calls real demos make.
    /// </summary>
    public const string CatalogDemo = "catalog-svc-demo-circuit";
}
