using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Top-level DI orchestrator. Calls per-track stubs in DependencyInjection.&lt;TrackId&gt;.cs.
/// Written ONCE at L0 by 'wave run'; not modified by L1 tracks.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCdcApplication(this IServiceCollection services) => services
        .AddCdcT1()
        .AddCdcT2()
        .AddCdcT3()
        .AddCdcT4()
        .AddCdcT5();
}
