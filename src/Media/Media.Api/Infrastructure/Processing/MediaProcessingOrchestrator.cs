using Haworks.Contracts.Media;
using Haworks.Media.Api.Options;
using Microsoft.Extensions.Options;

namespace Haworks.Media.Api.Infrastructure.Processing;

public sealed class MediaProcessingOrchestrator : IDisposable
{
    private readonly IEnumerable<IMediaProcessor> _processors;
    private readonly ILogger<MediaProcessingOrchestrator> _logger;
    private readonly SemaphoreSlim _concurrencyGate;

    public MediaProcessingOrchestrator(
        IEnumerable<IMediaProcessor> processors,
        IOptions<TranscodeOptions> opts,
        ILogger<MediaProcessingOrchestrator> logger)
    {
        _processors = processors;
        _logger = logger;
        _concurrencyGate = new SemaphoreSlim(opts.Value.MaxConcurrentJobs, opts.Value.MaxConcurrentJobs);
    }

    public async Task<IReadOnlyList<MediaVariant>> ProcessAsync(
        Guid mediaId, string s3Key, string mimeType, CancellationToken ct)
    {
        var applicableProcessors = _processors.Where(p => p.CanProcess(mimeType)).ToList();

        if (applicableProcessors.Count == 0)
        {
            _logger.LogInformation("No processors for {MimeType} — file {MediaId} served as-is", mimeType, mediaId);
            return Array.Empty<MediaVariant>();
        }

        await _concurrencyGate.WaitAsync(ct);
        try
        {
            var allVariants = new List<MediaVariant>();

            foreach (var processor in applicableProcessors)
            {
                try
                {
                    var variants = await processor.ProcessAsync(mediaId, s3Key, mimeType, ct);
                    allVariants.AddRange(variants);
                    _logger.LogInformation("Processor {Type} generated {Count} variants for {MediaId}",
                        processor.GetType().Name, variants.Count, mediaId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Processor {Type} failed for {MediaId}", processor.GetType().Name, mediaId);
                    throw;
                }
            }

            return allVariants;
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    public void Dispose() => _concurrencyGate.Dispose();
}
