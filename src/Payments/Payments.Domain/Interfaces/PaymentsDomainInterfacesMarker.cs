namespace Haworks.Payments.Domain.Interfaces;

/// <summary>
/// Anchor type so the Haworks.Payments.Domain.Interfaces namespace exists
/// at the moment global usings are resolved (Application/Infrastructure
/// ship `global using Haworks.Payments.Domain.Interfaces;` from Phase 3a).
/// Real repository interfaces (IPaymentRepository, IPaymentSnapshot, …)
/// land in Phase 3b.
/// </summary>
internal static class PaymentsDomainInterfacesMarker
{
}
