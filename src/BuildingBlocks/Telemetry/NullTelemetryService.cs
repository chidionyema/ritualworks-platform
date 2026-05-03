using Haworks.BuildingBlocks.Telemetry;

namespace Haworks.BuildingBlocks.Telemetry;

/// <summary>
/// No-op implementation of ITelemetryService for when telemetry is not configured.
/// </summary>
public class NullTelemetryService : ITelemetryService
{
    public static readonly NullTelemetryService Instance = new();

    private NullTelemetryService() { }

    public void TrackEvent(string eventName) { }
    public void TrackEvent(string eventName, IDictionary<string, object>? properties) { }
    public void TrackEvent(string eventName, IDictionary<string, string>? properties) { }
    public void TrackException(Exception ex) { }
    public void TrackException(Exception exception, IDictionary<string, object>? properties) { }
    public void TrackMetric(string metricName, double value, IDictionary<string, string>? properties = null) { }
    public IDisposable TrackDependency(string dependencyType, string dependencyName) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        private NullDisposable() { }
        public void Dispose() { }
    }
}
