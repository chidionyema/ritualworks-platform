using System.Threading.Channels;
using FluentValidation;
using Haworks.Audit.Application.Export;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Haworks.Audit.Application;

public static class AuditExportRegistration
{
    public static IServiceCollection AddAuditExport(this IServiceCollection services)
    {
        // Validators
        services.AddScoped<IValidator<AuditExportRequest>, AuditExportRequestValidator>();

        // Internal queue for export jobs
        var channel = Channel.CreateUnbounded<Guid>();
        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);

        // Register implementations via reflection to avoid circular dependency
        // between Application and Infrastructure projects.
        var infraAssembly = "Haworks.Audit.Infrastructure";

        var serviceType = Type.GetType($"Haworks.Audit.Infrastructure.Export.AuditExportJobService, {infraAssembly}");
        if (serviceType != null)
        {
            services.AddScoped(typeof(IAuditExportJob), serviceType);
        }

        var workerType = Type.GetType($"Haworks.Audit.Infrastructure.Export.AuditExportWorker, {infraAssembly}");
        if (workerType != null)
        {
            services.AddSingleton(typeof(IHostedService), sp => ActivatorUtilities.CreateInstance(sp, workerType));
        }

        var rolloverType = Type.GetType($"Haworks.Audit.Infrastructure.Partitions.PartitionRolloverService, {infraAssembly}");
        if (rolloverType != null)
        {
            services.AddSingleton(typeof(IHostedService), sp => ActivatorUtilities.CreateInstance(sp, rolloverType));
        }

        // Register S3 client for exports
        var s3ClientType = Type.GetType($"Amazon.S3.AmazonS3Client, AWSSDK.S3");
        var s3InterfaceType = Type.GetType($"Amazon.S3.IAmazonS3, AWSSDK.S3");
        var s3ConfigType = Type.GetType($"Amazon.S3.AmazonS3Config, AWSSDK.S3");

        if (s3ClientType != null && s3InterfaceType != null && s3ConfigType != null)
        {
            services.AddSingleton(s3InterfaceType, sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var serviceUrl = config.GetConnectionString("s3")
                    ?? throw new InvalidOperationException("ConnectionStrings:s3 must be configured for audit export");

                var s3Config = Activator.CreateInstance(s3ConfigType);
                s3ConfigType.GetProperty("ServiceURL")?.SetValue(s3Config, serviceUrl);
                s3ConfigType.GetProperty("ForcePathStyle")?.SetValue(s3Config, true);

                return Activator.CreateInstance(s3ClientType, s3Config)!;
            });
        }

        return services;
    }
}
