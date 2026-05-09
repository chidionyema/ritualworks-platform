using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Infrastructure.Channels.Push;
using Haworks.Notifications.Infrastructure.Channels.Push.Fcm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Haworks.Notifications.Infrastructure;

public static class PushChannelServiceCollectionExtensions
{
    public static IServiceCollection AddFcmPushProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<Haworks.Notifications.Infrastructure.Channels.Push.Fcm.FcmOptions>()
            .Bind(configuration.GetSection(Haworks.Notifications.Infrastructure.Channels.Push.Fcm.FcmOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<FirebaseApp>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<Haworks.Notifications.Infrastructure.Channels.Push.Fcm.FcmOptions>>().Value;
            
            // FirebaseApp.Create throws if app with same name exists.
            // In typical production, DefaultInstance is enough.
            var app = FirebaseApp.DefaultInstance;
            if (app == null)
            {
#pragma warning disable CS0618
                app = FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.FromJson(opts.ServiceAccountJson),
                    ProjectId = opts.ProjectId
                });
#pragma warning restore CS0618
            }
            return app;
        });

        services.AddSingleton<FirebaseMessaging>(sp =>
        {
            var app = sp.GetRequiredService<FirebaseApp>();
            return FirebaseMessaging.GetMessaging(app);
        });

        services.AddScoped<IPushProvider, FcmPushProvider>();

        return services;
    }

    public static IServiceCollection AddNotificationPushChannel(this IServiceCollection services)
    {
        services.AddScoped<IPushChannelGateway, PushChannelGateway>();
        return services;
    }
}
