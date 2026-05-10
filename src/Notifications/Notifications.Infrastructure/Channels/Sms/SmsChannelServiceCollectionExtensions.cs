using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Infrastructure.Channels.Sms;
using Haworks.Notifications.Infrastructure.Channels.Sms.Twilio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Twilio.Clients;

namespace Haworks.Notifications.Infrastructure;

public static class SmsChannelServiceCollectionExtensions
{
    public static IServiceCollection AddTwilioSmsProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TwilioOptions>()
            .Bind(configuration.GetSection(TwilioOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ITwilioRestClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<TwilioOptions>>().Value;
            return new TwilioRestClient(opts.AccountSid, opts.AuthToken);
        });

        services.AddScoped<ISmsProvider, TwilioSmsProvider>();

        return services;
    }

    public static IServiceCollection AddNotificationSmsChannel(this IServiceCollection services)
    {
        services.AddScoped<ISmsChannelGateway, SmsChannelGateway>();
        return services;
    }
}
