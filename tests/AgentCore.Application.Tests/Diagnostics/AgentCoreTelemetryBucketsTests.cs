using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using AgentCore.Application.Diagnostics;
using Xunit;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>
    /// Every <c>agentcore.*</c> histogram carries unit <c>s</c>, so the .NET SDK's default millisecond-shaped bucket
    /// bounds <c>[0,5,10,...,10000]</c> would put every reading in the first bucket or two and make a percentile
    /// query meaningless (audit finding F1, docs/probes/r2/audit/AUDIT.md). Each histogram gives bounds in seconds
    /// as <c>InstrumentAdvice&lt;double&gt;</c> on <c>CreateHistogram</c>; <see cref="Histogram{T}.Advice"/> is how
    /// a listener -- and the OTel SDK itself -- reads them back without needing a full exporter (confirmed against
    /// OpenTelemetry 1.17.0 in a scratch probe: the exported <c>explicitBounds</c> equal the advice given here).
    /// </summary>
    public sealed class AgentCoreTelemetryBucketsTests
    {
        private static readonly double[] OperationDurationBounds =
            [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92];

        private static readonly double[] TimeToFirstTokenBounds =
            [0.001, 0.005, 0.01, 0.02, 0.04, 0.06, 0.08, 0.1, 0.25, 0.5, 0.75, 1.0, 2.5, 5.0, 7.5, 10.0];

        /// <summary>Each histogram of this library, beside the bounds its own kind of reading takes.</summary>
        public static TheoryData<string, double[]> Histograms =>
            new()
            {
                { "agentcore.turn.duration", OperationDurationBounds },
                { "agentcore.turn.time_to_reply_end", OperationDurationBounds },
                { "agentcore.turn.time_to_first_token", TimeToFirstTokenBounds },
                { "agentcore.turn.time_to_first_speech", TimeToFirstTokenBounds },
                { "agentcore.barge_in.latency", TimeToFirstTokenBounds },
            };

        [Theory]
        [MemberData(nameof(Histograms))]
        public void AHistogram_AdvisesTheSecondsBoundsOfItsGenAiCounterpart(string name, double[] expectedBounds)
        {
            double[]? bounds = null;
            using MeterListener listener = new()
            {
                InstrumentPublished = (instrument, active) =>
                {
                    if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal)
                        && string.Equals(instrument.Name, name, StringComparison.Ordinal)
                        && instrument is Histogram<double> histogram)
                    {
                        bounds = histogram.Advice?.HistogramBucketBoundaries?.ToArray();
                    }

                    // A published instrument is never measured here: reading Advice is enough, and firing a
                    // real measurement would touch every other test's process-wide meter for nothing.
                },
            };

            listener.Start();

            // MeterName is a const, inlined at every call site, so referencing it above never runs
            // AgentCoreTelemetry's static field initializers. This forces them, the same way any earlier
            // turn in the process would: the instruments are process-wide singletons and only publish once.
            RuntimeHelpers.RunClassConstructor(typeof(AgentCoreTelemetry).TypeHandle);

            Assert.NotNull(bounds);
            Assert.Equal(expectedBounds, bounds);
        }
    }
}
