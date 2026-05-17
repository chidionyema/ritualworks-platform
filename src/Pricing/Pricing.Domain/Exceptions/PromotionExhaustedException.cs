namespace Haworks.Pricing.Domain.Exceptions;

/// <summary>
/// Thrown when a promotion code has reached its maximum redemptions.
/// </summary>
public sealed class PromotionExhaustedException : Exception
{
    public string Code { get; }

    public PromotionExhaustedException(string code)
        : base($"Promotion code '{code}' has been exhausted.")
    {
        Code = code;
    }
}
