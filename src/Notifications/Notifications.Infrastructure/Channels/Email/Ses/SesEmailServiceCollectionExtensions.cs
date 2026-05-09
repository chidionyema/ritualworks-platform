using Amazon;
using Amazon.SimpleEmailV2;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Infrastructure.Channels.Email.Ses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Haworks.Notifications.Infrastructure;

public static class SesEmailServiceCollectionExtensions
{
    public static IServiceCollection AddSesEmailProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SesOptions>()
            .Bind(configuration.GetSection(SesOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IAmazonSimpleEmailServiceV2>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SesOptions>>().Value;
            var awsCfg = new AmazonSimpleEmailServiceV2Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(opts.Region)
            };
            return new AmazonSimpleEmailServiceV2Client(opts.AccessKey, opts.SecretKey, awsCfg);
        });

        services.AddScoped<IEmailProvider, SesEmailProvider>();

        return services;
    }
}
