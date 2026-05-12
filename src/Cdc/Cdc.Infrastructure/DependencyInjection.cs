using Haworks.Cdc.Application.Interfaces;
using Haworks.Cdc.Infrastructure.Replication;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCdcInfrastructure(this IServiceCollection services)
    {
        services.AddTransient<PgOutputDecoder>();
        services.AddScoped<ICdcRelay, PostgresLogicalReplicationSubscriber>();
        
        return services;
    }
}
