using Haworks.Payments.Domain;

namespace Haworks.Payments.Application.Common;

/// <summary>
/// Provider-agnostic static helper methods for payment validation.
/// </summary>
public static class PaymentValidationHelper
{
    private const decimal DefaultAmountTolerance = 0.02m;

    /// <summary>
    /// Checks if a payment has already been processed.
    /// </summary>
    public static bool IsAlreadyProcessed(Payment payment) =>
        payment.Status == PaymentStatus.Completed;

    /// <summary>
    /// Checks if a payment is flagged for review.
    /// </summary>
    public static bool IsFlaggedForReview(Payment payment) =>
        payment.Status == PaymentStatus.Flagged;

    /// <summary>
    /// Calculates the amount difference and checks if it exceeds tolerance.
    /// </summary>
    public static bool HasAmountMismatch(
        decimal actualPaid,
        decimal expectedTotal,
        decimal tolerance = 0m)
    {
        var effectiveTolerance = tolerance > 0 ? tolerance : DefaultAmountTolerance;
        return Math.Abs(actualPaid - expectedTotal) > effectiveTolerance;
    }

    /// <summary>
    /// Validates that the payment currency matches the expected currency.
    /// </summary>
    public static bool HasCurrencyMismatch(string? actualCurrency, string? expectedCurrency)
    {
        if (string.IsNullOrWhiteSpace(actualCurrency) || string.IsNullOrWhiteSpace(expectedCurrency))
            return true;

        return !string.Equals(actualCurrency, expectedCurrency, StringComparison.OrdinalIgnoreCase);
    }
}
