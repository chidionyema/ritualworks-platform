using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Infrastructure.Channels.Email.SendGrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SendGrid;

namespace Haworks.Notifications.Infrastructure;

public static class SendGridEmailServiceCollectionExtensions
{
    public static IServiceCollection AddSendGridEmailProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SendGridOptions>()
            .Bind(configuration.GetSection(SendGridOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ISendGridClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SendGridOptions>>().Value;
            return new SendGridClient(opts.ApiKey);
        });

        services.AddScoped<IEmailProvider, SendGridEmailProvider>();

        return services;
    }
}
