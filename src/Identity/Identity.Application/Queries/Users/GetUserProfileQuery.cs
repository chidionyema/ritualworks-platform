using Haworks.Identity.Application.DTOs;
using Haworks.BuildingBlocks.Common;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace Haworks.Identity.Application.Queries.Users;

public sealed record GetUserProfileQuery(string UserId) : IRequest<Result<UserProfileDto>>;

internal sealed class GetUserProfileQueryHandler : IRequestHandler<GetUserProfileQuery, Result<UserProfileDto>>
{
    private readonly UserManager<User> _userManager;
    private readonly IUserProfileRepository _userProfileRepository;

    public GetUserProfileQueryHandler(
        UserManager<User> userManager,
        IUserProfileRepository userProfileRepository)
    {
        _userManager = userManager;
        _userProfileRepository = userProfileRepository;
    }

    public async Task<Result<UserProfileDto>> Handle(
        GetUserProfileQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.UserId))
        {
            return Result.Failure<UserProfileDto>(
                Error.Validation("Users.MissingUserId", "User ID is required."));
        }

        var user = await _userManager.FindByIdAsync(request.UserId);
        var profile = await _userProfileRepository.GetByUserIdAsync(request.UserId, cancellationToken);

        if (profile == null)
        {
            return Result.Success(new UserProfileDto
            {
                FirstName = string.Empty,
                LastName = string.Empty,
                Email = user?.Email,
                Phone = string.Empty,
                Address = string.Empty,
                City = string.Empty,
                State = string.Empty,
                PostalCode = string.Empty,
                Country = "US",
                Bio = string.Empty,
                Website = string.Empty,
                AvatarUrl = string.Empty
            });
        }

        return Result.Success(new UserProfileDto
        {
            FirstName = profile.FirstName ?? string.Empty,
            LastName = profile.LastName ?? string.Empty,
            Email = user?.Email,
            Phone = profile.Phone ?? string.Empty,
            Address = profile.Address ?? string.Empty,
            City = profile.City ?? string.Empty,
            State = profile.State ?? string.Empty,
            PostalCode = profile.PostalCode ?? string.Empty,
            Country = profile.Country ?? "US",
            Bio = profile.Bio ?? string.Empty,
            Website = profile.Website ?? string.Empty,
            AvatarUrl = profile.AvatarUrl ?? string.Empty
        });
    }
}
