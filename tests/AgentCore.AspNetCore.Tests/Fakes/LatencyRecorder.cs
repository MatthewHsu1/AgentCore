using AgentCore.Application.Hooks.Notices;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Keeps every latency reading a voice reply reports, in seconds, by metric.</summary>
    internal sealed class LatencyRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<(LatencyMetric Metric, double Seconds)> _readings = [];

        public void Record(LatencyMetric metric, TimeSpan value, int? _)
        {
            lock (_gate)
            {
                _readings.Add((metric, value.TotalSeconds));
            }
        }

        public IReadOnlyList<double> Of(LatencyMetric metric)
        {
            lock (_gate)
            {
                return [.. _readings.Where(reading => reading.Metric == metric).Select(reading => reading.Seconds)];
            }
        }
    }
}
