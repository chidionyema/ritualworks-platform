using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain.Interfaces;
using MediatR;

namespace Haworks.Identity.Application.Commands.Users;

public sealed record SaveShippingInfoCommand(
    string UserId,
    string FirstName,
    string LastName,
    string Address,
    string City,
    string? State,
    string PostalCode,
    string Country,
    string? Phone,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result>;

internal sealed class SaveShippingInfoCommandHandler : IRequestHandler<SaveShippingInfoCommand, Result>
{
    private readonly IUserProfileRepository _userProfileRepository;

    public SaveShippingInfoCommandHandler(IUserProfileRepository userProfileRepository)
    {
        _userProfileRepository = userProfileRepository;
    }

    public async Task<Result> Handle(SaveShippingInfoCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.UserId))
        {
            return Result.Failure(Error.Users.MissingUserId);
        }

        var profile = await _userProfileRepository.GetByUserIdAsync(request.UserId, cancellationToken);

        if (profile == null)
        {
            profile = UserProfile.Create(request.UserId);
            await _userProfileRepository.AddAsync(profile, cancellationToken);
        }

        profile.UpdatePersonalInfo(request.FirstName, request.LastName, request.Phone);
        profile.UpdateAddress(
            request.Address,
            request.City,
            request.State ?? string.Empty,
            request.PostalCode,
            request.Country);

        await _userProfileRepository.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
