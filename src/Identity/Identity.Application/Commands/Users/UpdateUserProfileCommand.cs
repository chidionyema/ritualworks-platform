using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain.Interfaces;
using MediatR;

namespace Haworks.Identity.Application.Commands.Users;

public sealed record UpdateUserProfileCommand(
    string UserId,
    string FirstName,
    string LastName,
    string? Phone,
    string? Address,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    string? Bio,
    string? Website,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result>;

internal sealed class UpdateUserProfileCommandHandler : IRequestHandler<UpdateUserProfileCommand, Result>
{
    private readonly IUserProfileRepository _userProfileRepository;

    public UpdateUserProfileCommandHandler(
        IUserProfileRepository userProfileRepository)
    {
        _userProfileRepository = userProfileRepository;
    }

    public async Task<Result> Handle(UpdateUserProfileCommand request, CancellationToken cancellationToken)
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
            request.Address ?? string.Empty,
            request.City ?? string.Empty,
            request.State ?? string.Empty,
            request.PostalCode ?? string.Empty,
            request.Country ?? "US");
        profile.UpdateProfileInfo(request.Bio, request.Website);

        await _userProfileRepository.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
