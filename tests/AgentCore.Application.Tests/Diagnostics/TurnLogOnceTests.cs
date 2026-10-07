using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Domain;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Diagnostics.TurnObservabilityHarness;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>
    /// The cases that log once: a failed extraction, an empty reply, the fourth tool failure, and a
    /// guard that throws.
    /// </summary>
    public sealed class TurnLogOnceTests
    {
        [Fact]
        public async Task AFailedExtraction_IsLoggedOnceForTheTurnAndTheConversationContinues()
        {
            RecordingLogger logger = new();
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new("I am sorry, I cannot do that.");
            ConversationSession session = Build(PolicyYaml, reply, fill, logger: logger).Create("conversation-x");

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            await session.FlushNoticesAsync();

            // A failed extraction leaves the slots unchanged, logs once for the turn, and continues.
            LogLine line = Assert.Single(logger.Of(1));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Contains("conversation-x", line.Message, StringComparison.Ordinal);
            Assert.NotNull(turn.ExtractionFailure);
            Assert.Equal("hello there.", turn.ReplyText);
        }

        [Fact]
        public async Task AnEmptyReply_IsLoggedOnce()
        {
            RecordingLogger logger = new();
            using SequencedChatClient reply = new("   ");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill, logger: logger).Create("conversation-x");

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            await session.FlushNoticesAsync();

            LogLine line = Assert.Single(logger.Of(3));
            Assert.Equal(LogLevel.Warning, line.Level);

            // The empty reply is not a tool fault, so the tool row stays silent.
            Assert.Empty(logger.Of(2));
        }

        [Fact]
        public async Task TheFourthConsecutiveToolFailure_IsLoggedOnce()
        {
            RecordingLogger logger = new();
            using LoopingToolCallingChatClient reply = new();
            ConversationSession session = Build(ToolYaml, reply, null, new ThrowingToolBuilder().Create, logger: logger).Create("conversation-x");

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);
            await session.FlushNoticesAsync();

            // The turn spoke the fallback, and the conversation is still alive.
            //
            // ONE line, and the turn spent four tool calls to get here. Each of those four is a row in
            // the audit chain, which is where a record of a conversation belongs; the log gets the turn-level
            // fact alone.
            LogLine line = Assert.Single(logger.Of(2));
            Assert.Equal(LogLevel.Error, line.Level);

            // The message names the turn and never the fault: the fault's message and stack trace ride
            // the Exception object instead (spans carry the type, logs carry the full error), so a span
            // never has to repeat what the log already owns.
            Assert.DoesNotContain(ThrowingToolBuilder.Message, line.Message, StringComparison.Ordinal);
            Assert.NotNull(line.Exception);
            Assert.Contains(ThrowingToolBuilder.Message, line.Exception!.Message, StringComparison.Ordinal);
            Assert.False(session.IsComplete);

            // The tool row and the empty-reply row are different failures, and only one of them fired.
            Assert.Empty(logger.Of(3));
        }

        [Fact]
        public async Task ASessionWithNoLogger_RunsATurnAndThrowsNothing()
        {
            using SequencedChatClient reply = new("   ");
            using SequencedChatClient fill = new("I am sorry, I cannot do that.");
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            // Two of the three "log once" rows fire in this one turn, and neither has anywhere to write.
            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.NotNull(turn.ExtractionFailure);
        }

        [Fact]
        public void AGuardThatDoesNotParse_IsLoggedOnceUnderItsName()
        {
            RecordingLogger logger = new();
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(
                """
            apiVersion: agentcore/v1
            guards:
              impossible: { "no_such_operator": [ 1, 2 ] }
            agents:
              items:
                - { id: only, instructions: "I answer everything" }
            entries:
              main:
                agent: only
            """);

            _ = new GuardEvaluator(document.Guards, logger);

            LogLine line = Assert.Single(logger.Of(4));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Contains("impossible", line.Message, StringComparison.Ordinal);
            Assert.NotNull(line.Exception);
        }
    }
}
