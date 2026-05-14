using Haworks.Search.Application.Consumers;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Search.Application;

public static class CdcRegistration
{
    public static IServiceCollection AddCdcSearchIndexing(this IServiceCollection services)
    {
        services.AddHostedService<CdcSearchIndexWorker>();
        return services;
    }
}
