namespace Haworks.BuildingBlocks.Telemetry
{
    public interface ITelemetryService
    {
        /// <summary>
        /// Track an event with a name.
        /// </summary>
        void TrackEvent(string eventName);

        /// <summary>
        /// Track an event with a name and a dictionary of properties (object values).
        /// </summary>
        void TrackEvent(string eventName, IDictionary<string, object>? properties);

        /// <summary>
        /// Track an event with a name and a dictionary of properties (string values).
        /// </summary>
        void TrackEvent(string eventName, IDictionary<string, string>? properties);

        /// <summary>
        /// Track an exception.
        /// </summary>
        void TrackException(Exception ex);

        /// <summary>
        /// Track an exception with additional context properties.
        /// </summary>
        void TrackException(Exception exception, IDictionary<string, object>? properties);

        /// <summary>
        /// Tracks a numeric metric value.
        /// </summary>
        void TrackMetric(string metricName, double value, IDictionary<string, string>? properties = null);

        /// <summary>
        /// Starts a dependency tracking operation.
        /// Call Dispose() on the returned object when the operation completes.
        /// </summary>
        IDisposable TrackDependency(string dependencyType, string dependencyName);
    }
}
