using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T5 — owned by L1 track T5. Replace the stub body with this track's
/// service registrations. L0 ships the empty stub so the orchestrator compiles.
/// </summary>
public static class CdcT5Registration
{
    public static IServiceCollection AddCdcT5(this IServiceCollection services) => services;
}
