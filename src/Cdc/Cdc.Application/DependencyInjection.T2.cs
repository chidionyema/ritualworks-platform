using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T2 — owned by L1 track T2. Replace the stub body with this track's
/// service registrations. L0 ships the empty stub so the orchestrator compiles.
/// </summary>
public static class CdcT2Registration
{
    public static IServiceCollection AddCdcT2(this IServiceCollection services) => services;
}
