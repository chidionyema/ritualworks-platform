using Haworks.BuildingBlocks.Common;
using Haworks.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace Haworks.Identity.Application;

public sealed record GetAvailableProvidersQuery : IRequest<Result<List<ExternalProviderDto>>>;

public sealed record ExternalProviderDto(string Name, string? DisplayName);

internal sealed class GetAvailableProvidersQueryHandler
    : IRequestHandler<GetAvailableProvidersQuery, Result<List<ExternalProviderDto>>>
{
    private readonly SignInManager<User> _signInManager;

    public GetAvailableProvidersQueryHandler(SignInManager<User> signInManager)
    {
        _signInManager = signInManager;
    }

    public async Task<Result<List<ExternalProviderDto>>> Handle(
        GetAvailableProvidersQuery request,
        CancellationToken cancellationToken)
    {
        var providers = (await _signInManager.GetExternalAuthenticationSchemesAsync())
            .Select(p => new ExternalProviderDto(p.Name, p.DisplayName))
            .ToList();

        return Result.Success(providers);
    }
}
