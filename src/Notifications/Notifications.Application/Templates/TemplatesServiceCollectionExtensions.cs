using Haworks.Notifications.Application.Templates;
using Microsoft.Extensions.DependencyInjection;

// Lives under /Templates (track L1.B's owned subdir) but declares the
// parent namespace `Haworks.Notifications.Application` so the existing
// call-site `services.AddNotificationTemplates()` in DependencyInjection.cs
// resolves to this implementation without an extra `using` (which would
// require touching shared files outside L1.B's owned-paths set).
//
// REPLACES the L0 stub of the same name in DependencyInjection.Stubs.cs
// (the stub line was deleted per the L1.B brief — see the comment marker
// in that file).
namespace Haworks.Notifications.Application;

/// <summary>
/// Composition root for the L1.B template engine — registers the Scriban-backed
/// renderer and the locale-aware selector. Repository registration lives in
/// the Infrastructure layer (<c>AddNotificationTemplatesPersistence</c>).
/// </summary>
internal static class TemplatesServiceCollectionExtensions
{
    internal static IServiceCollection AddNotificationTemplates(this IServiceCollection services)
    {
        services.AddScoped<ITemplateRenderer, ScribanTemplateRenderer>();
        services.AddScoped<ITemplateSelector, TemplateSelector>();
        return services;
    }
}
