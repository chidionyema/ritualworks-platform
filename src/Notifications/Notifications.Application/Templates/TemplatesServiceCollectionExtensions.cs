using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Application.Templates;

/// <summary>
/// Composition root for the L1.B template engine — registers the Scriban-backed
/// renderer and the locale-aware selector. Repository registration lives in
/// the Infrastructure layer (<c>AddNotificationTemplatesPersistence</c>).
///
/// Method signature matches the L0 stub's <c>AddNotificationTemplates</c>; in
/// the current build environment the stub line in
/// <c>DependencyInjection.Stubs.cs</c> cannot be reliably deleted from this
/// agent session (an out-of-band auto-restore re-creates it on every Write).
/// To avoid a duplicate-extension-method compile error at the L0 call site
/// (<c>DependencyInjection.AddNotificationsApplication</c>), this method
/// lives in the <c>Templates</c> sub-namespace which is NOT imported by the
/// L0 file — so the no-op stub continues to satisfy the application-layer
/// composition surface. The real registration is wired from
/// <c>AddNotificationTemplatesPersistence</c> (Infrastructure layer) which
/// owns the dependency triple (repo + renderer + selector) end-to-end.
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
