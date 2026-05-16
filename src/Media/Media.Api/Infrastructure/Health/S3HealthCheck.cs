using Amazon.S3;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Haworks.Media.Api.Infrastructure.Health;

public sealed class S3HealthCheck(IAmazonS3 s3, IOptions<MediaStorageOptions> opts) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        if (!opts.Value.Enabled)
            return HealthCheckResult.Healthy("S3 disabled");

        try
        {
            await s3.EnsureBucketExistsAsync(opts.Value.BucketName);
            return HealthCheckResult.Healthy($"S3 bucket '{opts.Value.BucketName}' accessible");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("S3 unreachable", ex);
        }
    }
}
