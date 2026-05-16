using FluentValidation;
using Haworks.Identity.Application.Commands.Auth;

namespace Haworks.Identity.Application.Validators.Auth;

/// <summary>
/// CreateServiceTokenCommand has no parameters — validator exists so every
/// IRequest type is covered and the validation pipeline never short-circuits.
/// </summary>
internal sealed class CreateServiceTokenCommandValidator : AbstractValidator<CreateServiceTokenCommand>
{
    public CreateServiceTokenCommandValidator()
    {
    }
}
