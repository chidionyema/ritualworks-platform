using Haworks.Notifications.Application.Templates;
using Microsoft.Extensions.DependencyInjection;

// File lives under /Persistence/Templates (track L1.B's owned subdir) but
// declares the parent `Haworks.Notifications.Infrastructure` namespace so
// the existing call-site `services.AddNotificationTemplatesPersistence()` in
// DependencyInjection.cs resolves to this implementation without an extra
// `using` (which would require touching shared files outside L1.B's
// owned-paths set).
//
// REPLACES the L0 stub of the same name in DependencyInjection.Stubs.cs
// (the stub line was deleted per the L1.B brief — see the comment marker
// in that file).
namespace Haworks.Notifications.Infrastructure;

/// <summary>
/// Composition root for L1.B template persistence — registers the EF Core
/// <see cref="ITemplateRepository"/> implementation against
/// <see cref="Haworks.Notifications.Infrastructure.Persistence.NotificationsDbContext"/>.
/// </summary>
internal static class TemplatesPersistenceServiceCollectionExtensions
{
    internal static IServiceCollection AddNotificationTemplatesPersistence(this IServiceCollection services)
    {
        services.AddScoped<ITemplateRepository, Haworks.Notifications.Infrastructure.Persistence.Templates.TemplateRepository>();
        return services;
    }
}
