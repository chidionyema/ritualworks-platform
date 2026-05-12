using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T1 — owned by L1 track T1. Replace the stub body with this track's
/// service registrations. L0 ships the empty stub so the orchestrator compiles.
/// </summary>
public static class CdcT1Registration
{
    public static IServiceCollection AddCdcT1(this IServiceCollection services) => services;
}
