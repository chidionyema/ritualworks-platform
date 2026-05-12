using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T4 — owned by L1 track T4. Replace the stub body with this track's
/// service registrations. L0 ships the empty stub so the orchestrator compiles.
/// </summary>
public static class CdcT4Registration
{
    public static IServiceCollection AddCdcT4(this IServiceCollection services) => services;
}
