namespace Haworks.Payments.Domain;

/// <summary>
/// Anchor type so the Haworks.Payments.Domain namespace exists at the
/// moment global usings are resolved by Application/Infrastructure
/// (which ship `global using Haworks.Payments.Domain;` before any real
/// entity types might exist in early phases).
/// </summary>
public static class PaymentsDomainMarker
{
}
