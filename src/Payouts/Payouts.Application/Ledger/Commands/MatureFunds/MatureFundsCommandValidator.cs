using FluentValidation;

namespace Haworks.Payouts.Application.Ledger.Commands.MatureFunds;

/// <summary>
/// MatureFundsCommand has no parameters — validator exists so every
/// IRequest type is covered and the validation pipeline never short-circuits.
/// </summary>
internal sealed class MatureFundsCommandValidator : AbstractValidator<MatureFundsCommand>
{
    public MatureFundsCommandValidator()
    {
    }
}
