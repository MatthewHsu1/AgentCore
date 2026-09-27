using System.Diagnostics.Metrics;
using AgentCore.Application.Diagnostics;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Collects every measurement this library's histograms take while it is alive.</summary>
    internal sealed class LatencyReadings : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, double Value)> _readings = [];
        private readonly Lock _gate = new();

        public LatencyReadings()
        {
            _listener.InstrumentPublished = (instrument, active) =>
            {
                if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal))
                {
                    active.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                lock (_gate)
                {
                    _readings.Add((instrument.Name, value));
                }
            });

            _listener.Start();
        }

        /// <summary>Gets what one histogram was told, in order, rounded to the millisecond.</summary>
        /// <param name="instrument">The metric name.</param>
        /// <returns>The measurements, in seconds.</returns>
        public IReadOnlyList<double> Of(string instrument)
        {
            lock (_gate)
            {
                return [.. _readings
                    .Where(reading => string.Equals(reading.Instrument, instrument, StringComparison.Ordinal))
                    .Select(reading => Math.Round(reading.Value, 3))];
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
