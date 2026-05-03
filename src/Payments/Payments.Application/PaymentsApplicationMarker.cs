namespace Haworks.Payments.Application;

/// <summary>
/// Anchor type so the Haworks.Payments.Application namespace exists at
/// the moment global usings are resolved by Infrastructure (which ships
/// `global using Haworks.Payments.Application;` before any real type
/// might exist in early phases).
/// </summary>
public static class PaymentsApplicationMarker
{
}
