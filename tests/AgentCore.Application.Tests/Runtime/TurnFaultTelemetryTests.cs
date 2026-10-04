using System.Diagnostics;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Tests.Diagnostics;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// On a turn fault, the exception
    /// TYPE goes on the <c>agentcore.turn</c> span, and the exception OBJECT -- message and stack trace both
    /// -- goes to the Error log. Neither carries the other's cut.
    /// </summary>
    public sealed class TurnFaultTelemetryTests
    {
        [Fact]
        public async Task AToolFaultsMarkerMessage_NeverReachesTheSpan_AndAlwaysReachesTheLogsException()
        {
            RecordingLogger logger = new();
            List<Activity> spans = [];
            using ActivityListener listener = ListenToAgentCoreTelemetry(spans);

            using LoopingToolCallingChatClient reply = new();
            ThrowingToolBuilder tools = new();
            ConversationSession session = TurnObservabilityHarness.Build(ToolYaml, reply, null, tools.Create, logger: logger)
                .Create("conversation-" + Guid.NewGuid().ToString("N"));

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);
            await session.FlushNoticesAsync();

            // ThrowingToolBuilder.Message is the marker here: a fixed literal nothing else in this run
            // produces, thrown inside a real TimeoutException by the tool itself.
            List<Activity> snapshot;
            lock (spans)
            {
                snapshot = [.. spans];
            }

            Activity span = Assert.Single(snapshot, item => string.Equals(
                item.GetTagItem("gen_ai.conversation.id") as string, session.ConversationId, StringComparison.Ordinal));

            Assert.DoesNotContain(
                span.TagObjects, tag => tag.Value is string text && text.Contains(ThrowingToolBuilder.Message, StringComparison.Ordinal));
            Assert.Empty(span.Events);
            Assert.NotNull(span.StatusDescription);
            Assert.DoesNotContain(ThrowingToolBuilder.Message, span.StatusDescription, StringComparison.Ordinal);

            // The type, and only the type, stands in for it on the span.
            Assert.Equal(typeof(TimeoutException).FullName, span.StatusDescription);
            Assert.Equal(typeof(TimeoutException).FullName, span.GetTagItem("error.type"));

            LogLine line = Assert.Single(logger.Of(2));
            Assert.DoesNotContain(ThrowingToolBuilder.Message, line.Message, StringComparison.Ordinal);
            Assert.NotNull(line.Exception);
            Assert.IsType<TimeoutException>(line.Exception);
            Assert.Contains(ThrowingToolBuilder.Message, line.Exception!.Message, StringComparison.Ordinal);
        }

        /// <summary>Subscribes to the one activity source of this library, and no other.</summary>
        private static ActivityListener ListenToAgentCoreTelemetry(List<Activity> spans)
        {
            ActivityListener listener = new()
            {
                ShouldListenTo = source =>
                    string.Equals(source.Name, AgentCoreTelemetry.ActivitySourceName, StringComparison.Ordinal),
                Sample = (ref _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (spans)
                    {
                        spans.Add(activity);
                    }
                },
            };

            ActivitySource.AddActivityListener(listener);
            return listener;
        }
    }
}
