using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T3 — owned by L1 track T3. Replace the stub body with this track's
/// service registrations. L0 ships the empty stub so the orchestrator compiles.
/// </summary>
public static class CdcT3Registration
{
    public static IServiceCollection AddCdcT3(this IServiceCollection services) => services;
}
