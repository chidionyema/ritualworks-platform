using Haworks.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Startup;

public static class StartupExtensions
{
    private static readonly string[] ReadyTags = ["ready"];

    public static IServiceCollection AddStartupTaskRunner(this IServiceCollection services)
    {
        services.AddSingleton<StartupTaskRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<StartupTaskRunner>());
        services.AddHealthChecks().AddCheck<StartupReadinessHealthCheck>("startup", tags: ReadyTags);
        return services;
    }

    public static StartupTaskRunner AddMigrationTask<TContext>(this StartupTaskRunner runner) where TContext : DbContext
    {
        runner.AddTask(async (sp, ct) =>
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<TContext>>();
            await db.Database.MigrateWithRetryAsync(logger, ct);
        });
        return runner;
    }
}
