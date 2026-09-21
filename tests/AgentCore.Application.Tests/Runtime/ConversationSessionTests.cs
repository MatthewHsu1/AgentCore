using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Domain;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The turn loop. One session runs one conversation, and one turn walks the writers in one fixed order.
    /// </summary>
    /// <remarks>
    /// Every test here runs offline. There is no network conversation and no API key anywhere in this file.
    /// </remarks>
    public sealed class ConversationSessionTests
    {
        private const string PolicyYaml =
            """
          apiVersion: agentcore/v1
          state:
            callerSaidGoodbye:
              type: boolean
              default: false
              writer: extractor
              description: whether the caller said goodbye
            brand: { type: string, writer: const, value: sole }
            greetingTurns:
              type: integer
              default: 0
              writer: counter
              increment: { "===": [ { var: stage }, "greeting" ] }
          guards:
            saidGoodbye: { var: callerSaidGoodbye }
          extractor:
            model: { ref: fill }
            when: after_reply
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "greet the caller" }
              - { id: closer,  instructions: "close the conversation" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - id: greeting
                    agent: greeter
                    to: [ { stage: close, when: saidGoodbye } ]
                  - id: close
                    agent: closer
                    terminal: true
          """;

        private const string ReminderYaml =
            """
          apiVersion: agentcore/v1
          state:
            machineModel: { type: string, writer: extractor, description: the machine model }
            serialNumber: { type: string, writer: extractor, description: the serial number }
          guards:
            identified:
              and:
                - { "!!": [ { var: machineModel } ] }
                - { "!!": [ { var: serialNumber } ] }
          extractor:
            model: { ref: fill }
            when: after_reply
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "greet the caller" }
              - { id: closer,  instructions: "close the conversation" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - id: greeting
                    agent: greeter
                    to: [ { stage: close, when: identified } ]
                  - id: close
                    agent: closer
                    terminal: true
          """;

        private const string TwoStagesYaml =
            """
          apiVersion: agentcore/v1
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "I am the greeter" }
              - { id: closer,  instructions: "I am the closer" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - { id: greeting, agent: greeter, to: [ { stage: close } ] }
                  - { id: close,    agent: closer,  to: [ { stage: greeting } ] }
          """;

        private const string ToolYaml =
            """
          apiVersion: agentcore/v1
          state:
            orderStatus:       { type: string,  writer: tool, from: lookup_order.status }
            callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
            shippedTurns:
              type: integer
              default: 0
              writer: counter
              increment: { "===": [ { var: orderStatus }, "shipped" ] }
          guards:
            saidGoodbye: { var: callerSaidGoodbye }
          extractor:
            model: { ref: fill }
            when: after_reply
          tools:
            - { id: lookup_order, kind: builtin, uses: orders.read, description: "Look up an order by its id." }
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "greet the caller", tools: [ lookup_order ] }
              - { id: closer,  instructions: "close the conversation" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - { id: greeting, agent: greeter, to: [ { stage: close, when: saidGoodbye } ] }
                  - { id: close,    agent: closer,  terminal: true }
          """;

        private const string OneAgentYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        """;

        private const string StayingNull = /*lang=json,strict*/ """{ "callerSaidGoodbye": null }""";
        private const string SaidGoodbye = /*lang=json,strict*/ """{ "callerSaidGoodbye": true }""";

        // -------------------------------------------------------------------------------------------
        // One turn, end to end.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task ATurn_RunsTheAgentOfTheCurrentStageAndReportsWhatItDid()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal("conversation-1", turn.ConversationId);
            Assert.Equal(0, turn.TurnIndex);
            Assert.Equal("greeting", turn.StageBefore);
            Assert.Equal("greeting", turn.StageAfter);
            Assert.Equal("hello there.", turn.ReplyText);
            Assert.False(turn.IsTerminal);
            Assert.Null(turn.ExtractionFailure);
            Assert.Same(turn, session.LastTurn);
        }

        [Fact]
        public async Task ATurn_MakesTwoModelCalls_TheReplyAndTheExtractor()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // The extractor has no retry: one reply conversation, and one extractor call.
            Assert.Equal(1, reply.Calls);
            Assert.Equal(1, fill.Calls);
        }

        [Fact]
        public async Task TheTranscript_HoldsWhatTheCallerSaidAndWhatTheAgentAnswered()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal(2, session.Transcript.Count);
            Assert.Equal(ChatRole.User, session.Transcript[0].Role);
            Assert.Equal("hi", session.Transcript[0].Text);
            Assert.Equal(ChatRole.Assistant, session.Transcript[1].Role);
            Assert.Equal("hello there.", session.Transcript[1].Text);
        }

        [Fact]
        public async Task TheTranscript_HandsOutACopyAndNeverTheListTheSessionWritesTo()
        {
            using SequencedChatClient reply = new("hello there.", "and again.");
            using SequencedChatClient fill = new(StayingNull, StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            IReadOnlyList<ChatMessage> taken = session.Transcript;

            _ = await session.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // A live view would grow behind the reader's back, and an amendment splicing the list under
            // an enumeration would throw rather than return a torn conversation. What was handed out is
            // one whole conversation as it stood at one instant, and the session went on without it.
            Assert.Equal(2, taken.Count);
            Assert.Equal(4, session.Transcript.Count);
            Assert.NotSame(taken, session.Transcript);
        }

        // -------------------------------------------------------------------------------------------
        // The reminder rides one request.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task TheReminder_ReachesTheReplyAgentAndLeavesTheCallersMessageAlone()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ReminderYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // The stage waits on two slots the caller supplies, and no writer has filled either yet.
            Assert.Contains(UnfilledSlotReminder.OpenTag, reply.SystemText(0), StringComparison.Ordinal);
            Assert.Contains(
                "the machine model and the serial number", reply.SystemText(0), StringComparison.Ordinal);

            // The caller's utterance is the caller's. It goes to the model, to store 1 and to the
            // extractor as it was spoken.
            Assert.Equal("hi", reply.LastUserText(0));
            Assert.Equal("hi", session.Transcript[0].Text);
        }

        [Fact]
        public async Task TheReminder_DropsASlotOnceAWriterFillsIt()
        {
            using SequencedChatClient reply = new("hello there.", "still here.");
            using SequencedChatClient fill = new(/*lang=json,strict*/ """{ "machineModel": "F85" }""", StayingNull);
            ConversationSession session = Build(ReminderYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // Turn 2 still waits on the serial number, so the reminder survives and names only that one.
            Assert.Contains(
                "the machine model and the serial number", reply.SystemText(0), StringComparison.Ordinal);
            Assert.Contains(UnfilledSlotReminder.OpenTag, reply.SystemText(1), StringComparison.Ordinal);
            Assert.Contains("the serial number.", reply.SystemText(1), StringComparison.Ordinal);
            Assert.DoesNotContain("the machine model", reply.SystemText(1), StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheReminder_ReachesAStreamingTurnToo()
        {
            // The run is inside the async iterator, and an iterator restores its caller's execution
            // context at every yield. A per-turn ambient value has to be opened again for each round or
            // the streaming path silently loses the reminder the buffered path carries.
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ReminderYaml, reply, fill).Create();

            await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
            }

            Assert.Contains(UnfilledSlotReminder.OpenTag, reply.SystemText(0), StringComparison.Ordinal);
            Assert.Equal("hi", reply.LastUserText(0));
        }

        [Fact]
        public async Task TheReminder_NeverAsksTheCallerForAnInferredFlag()
        {
            // config/local.yaml ships this shape: one boolean the extractor infers from the turn, read by
            // the exit guard of the talking stage, carrying a default. Section 8.3 reminds on a slot that
            // is "still null", and a default is never null, so nothing is owed and the caller is not asked.
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            string turnContext = reply.SystemText(0);
            Assert.DoesNotContain(UnfilledSlotReminder.OpenTag, turnContext, StringComparison.Ordinal);
            Assert.DoesNotContain("callerSaidGoodbye", turnContext, StringComparison.Ordinal);
        }

        // -------------------------------------------------------------------------------------------
        // The session owns the transcript, and the stage switches the agent.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task TheStage_SwitchesTheAgentAndTheTranscriptCarriesTheWholeConversation()
        {
            using SequencedChatClient reply = new("first reply.", "second reply.");
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            _ = await session.RunTurnAsync("one", TestContext.Current.CancellationToken);
            Assert.Equal("close", session.Stage);

            _ = await session.RunTurnAsync("two", TestContext.Current.CancellationToken);
            Assert.Equal("greeting", session.Stage);

            // Each stage names its own agent, so the instructions change between the two requests.
            Assert.Contains("I am the greeter", reply.Options[0]!.Instructions, StringComparison.Ordinal);
            Assert.Contains("I am the closer", reply.Options[1]!.Instructions, StringComparison.Ordinal);

            // A session bound to one agent could not carry this. The turn loop owns the transcript, so
            // the second agent reads the first turn.
            Assert.Contains(reply.Requests[1], message => Contains(message, "one"));
            Assert.Contains(reply.Requests[1], message => Contains(message, "first reply."));
        }

        // -------------------------------------------------------------------------------------------
        // The writers, in order.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public void TheConstWriter_RunsOnceWhenTheSessionStarts()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);

            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            // A constant never changes, so it is written before the first turn and never again.
            Assert.Equal("sole", session.State.Read("brand")!.GetValue<string>());
            Assert.False(session.State.IsUnfilled("brand"));
        }

        [Fact]
        public async Task TheCounter_RunsBeforeTheTransitionAndReadsTheStageTheTurnSpokeIn()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult turn = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

            // The turn spoke in greeting and the machine then moved to close. The counter rule reads
            // stage === greeting, so a counter that ran after the transition would report zero.
            Assert.Equal("greeting", turn.StageBefore);
            Assert.Equal("close", turn.StageAfter);
            Assert.Equal(1, session.State.Read("greetingTurns")!.GetValue<long>());
        }

        [Fact]
        public async Task TheToolWriter_RunsBeforeTheCounterThatReadsItsSlot()
        {
            using ToolCallingChatClient reply = new("your order is on the way.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ToolYaml, reply, fill, new StubToolBuilder(/*lang=json,strict*/ """{ "status": "shipped" }""").Create).Create();

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            Assert.Equal(["lookup_order"], reply.Called);
            Assert.Equal("shipped", session.State.Read("orderStatus")!.GetValue<string>());

            // The counter rule reads orderStatus. It is one only because the tool writer went first.
            Assert.Equal(1, session.State.Read("shippedTurns")!.GetValue<long>());
            Assert.Equal("greeting", turn.StageAfter);
        }

        [Fact]
        public async Task TheToolWriter_ReadsAResultTheToolAnsweredAsText()
        {
            using ToolCallingChatClient reply = new("your order is on the way.");
            using SequencedChatClient fill = new(StayingNull);
            StubToolBuilder tools = new(/*lang=json,strict*/ """{ "status": "shipped" }""", asText: true);
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // A tool result has no declared shape, so a document answered as one string still resolves.
            Assert.Equal("shipped", session.State.Read("orderStatus")!.GetValue<string>());
        }

        [Fact]
        public async Task TheExtractor_RunsBeforeTheTransitionThatReadsItsSlot()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult turn = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

            // The guard reads callerSaidGoodbye, and only the extractor writes it.
            Assert.True(session.State.Read("callerSaidGoodbye")!.GetValue<bool>());
            Assert.Equal("close", turn.StageAfter);
            Assert.True(turn.IsTerminal);
            Assert.True(session.IsComplete);
        }

        [Fact]
        public async Task TheExtractor_ReadsTheFinishedTurn()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // extractor.when: after_reply. The request holds what the caller said and what the agent
            // answered, and the caller's message carries no reminder.
            List<ChatMessage> request = fill.Requests[0];
            Assert.Contains(request, message => Contains(message, "hi"));
            Assert.Contains(request, message => Contains(message, "hello there."));
            Assert.DoesNotContain(request, message => Contains(message, UnfilledSlotReminder.OpenTag));
        }

        [Fact]
        public async Task TheExtractor_ReadsTheAgentsPreviousMessageBeforeTheFinishedTurn()
        {
            using SequencedChatClient reply = new("is it the CT900 or the CT900ENT?", "got it.");
            using SequencedChatClient fill = new(StayingNull, StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("my belt slips", TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync("the ENT one", TestContext.Current.CancellationToken);

            // Turn 1 has no earlier agent message, so the finished turn stands alone.
            List<ChatMessage> first = [.. fill.Requests[0].Where(message => message.Role != ChatRole.System)];
            Assert.Equal([ChatRole.User, ChatRole.Assistant], first.Select(message => message.Role));

            // Turn 2: the caller is answering a question. Measured 2026-09-02: without the question in
            // view the extractor reads "the ENT one" and fills nothing; with it, it fills the machine.
            List<ChatMessage> second = [.. fill.Requests[1].Where(message => message.Role != ChatRole.System)];
            Assert.Equal([ChatRole.Assistant, ChatRole.User, ChatRole.Assistant], second.Select(message => message.Role));
            Assert.Equal("is it the CT900 or the CT900ENT?", second[0].Text);
            Assert.Equal("the ENT one", second[1].Text);
            Assert.Equal("got it.", second[2].Text);
        }

        [Fact]
        public async Task AFailedExtraction_DoesNotDropTheTurn()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new("I am sorry, I cannot do that.");
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // Section 8.7: the extractor never drops a conversation. The turn ends, and it carries the reason.
            Assert.NotNull(turn.ExtractionFailure);
            Assert.Equal("hello there.", turn.ReplyText);
            Assert.Equal("greeting", turn.StageAfter);
            Assert.True(session.State.IsUnfilled("callerSaidGoodbye"));
        }

        [Fact]
        public async Task ADocumentWithNoExtractor_RunsATurnAndReportsNoFailure()
        {
            using SequencedChatClient reply = new("first reply.");
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            TurnResult turn = await session.RunTurnAsync("one", TestContext.Current.CancellationToken);

            Assert.Null(turn.ExtractionFailure);
            Assert.Equal(1, reply.Calls);
        }

        // -------------------------------------------------------------------------------------------
        // The reserved slots.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task TheReservedSlots_CountTheFinishedTurnsAndReadTheInjectedClock()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            TestTimeProvider clock = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, timeProvider: clock).Create();

            Assert.Equal(0, session.State.TurnIndex);
            Assert.Equal(0, session.State.ConversationDurationSeconds);

            clock.Advance(TimeSpan.FromSeconds(12.5));
            TurnResult first = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal(0, first.TurnIndex);
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal(12.5, session.State.ConversationDurationSeconds);

            clock.Advance(TimeSpan.FromSeconds(7.5));
            TurnResult second = await session.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            Assert.Equal(1, second.TurnIndex);
            Assert.Equal(2, session.State.TurnIndex);
            Assert.Equal(20, session.State.ConversationDurationSeconds);
        }

        [Fact]
        public async Task TheStage_ReachesTheStateDocumentSoAGuardReadsIt()
        {
            using SequencedChatClient reply = new("first reply.");
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            _ = await session.RunTurnAsync("one", TestContext.Current.CancellationToken);

            Assert.Equal("close", session.Stage);
            Assert.Equal("close", session.State.Stage);
            Assert.Equal("close", session.State.Snapshot()[ReservedStateSlots.Stage]!.GetValue<string>());
        }

        // -------------------------------------------------------------------------------------------
        // A finished conversation.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task ACompleteConversation_RejectsAFurtherTurn()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);
            Assert.True(session.IsComplete);

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RunTurnAsync("hello again", TestContext.Current.CancellationToken));

            Assert.Contains("close", failure.Message, StringComparison.Ordinal);
            Assert.Equal(1, reply.Calls);
        }

        [Fact]
        public async Task ADocumentWithNoPolicy_RunsTheOneAgentAndNeverEnds()
        {
            using SequencedChatClient reply = new("I answered.");
            ConversationSession session = Build(OneAgentYaml, reply, null).Create();

            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, session.Stage);
            Assert.Equal(string.Empty, turn.StageBefore);
            Assert.Equal(string.Empty, turn.StageAfter);
            Assert.False(turn.IsTerminal);
            Assert.False(session.IsComplete);

            _ = await session.RunTurnAsync("again", TestContext.Current.CancellationToken);
            Assert.Equal(2, reply.Calls);
        }

        // -------------------------------------------------------------------------------------------
        // Streaming.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task Streaming_CarriesTheWholeReplyAndThenFinishesTheTurn()
        {
            using ScriptedChatClient reply = new("hello", " there.");
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("goodbye", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            string text = string.Concat(updates.Select(update => update.Text));
            Assert.Equal("hello there.", text);

            Assert.NotNull(session.LastTurn);
            Assert.Equal("hello there.", session.LastTurn.ReplyText);
            Assert.Equal("close", session.LastTurn.StageAfter);
            Assert.True(session.IsComplete);
        }

        [Fact]
        public async Task Streaming_MovesNothingUntilTheEnumerationCompletes()
        {
            using ScriptedChatClient reply = new("hello", " there.") { GateAfterFirstFragment = true };
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, TestContext.Current.CancellationToken);

            await using IAsyncEnumerator<ChatResponseUpdate> updates = session.RunTurnStreamingAsync("goodbye", linked.Token).GetAsyncEnumerator(linked.Token);

            // The model holds every fragment after the first, so this line runs mid-reply.
            Assert.True(await updates.MoveNextAsync());
            Assert.Equal("hello", updates.Current.Text);
            Assert.Null(session.LastTurn);
            Assert.Equal("greeting", session.Stage);
            Assert.Equal(0, session.State.TurnIndex);

            reply.OpenGate();
            while (await updates.MoveNextAsync())
            {
                // Drain the rest of the reply.
            }

            Assert.NotNull(session.LastTurn);
            Assert.Equal("close", session.Stage);
            Assert.Equal(1, session.State.TurnIndex);
        }

        [Fact]
        public async Task ASecondTurn_IsRejectedWhileTheFirstOneIsStillRunning()
        {
            using ScriptedChatClient reply = new("hello", " there.") { GateAfterFirstFragment = true };
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, TestContext.Current.CancellationToken);

            await using IAsyncEnumerator<ChatResponseUpdate> updates = session.RunTurnStreamingAsync("hi", linked.Token).GetAsyncEnumerator(linked.Token);
            Assert.True(await updates.MoveNextAsync());

            // The state document takes no lock, so a second turn must fail rather than corrupt it.
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RunTurnAsync("hi again", linked.Token));
            Assert.Contains("one turn at a time", failure.Message, StringComparison.Ordinal);

            reply.OpenGate();
            while (await updates.MoveNextAsync())
            {
                // Drain the rest of the reply.
            }

            Assert.NotNull(session.LastTurn);
        }

        // -------------------------------------------------------------------------------------------
        // Section 8.7, last row: the run returns quietly with no text.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task AnEmptyReply_SpeaksTheFallbackAndReportsWhyTheTurnFailed()
        {
            using SequencedChatClient reply = new("   ");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // Request 41 goes out with no tools and the run returns with no exception. On a voice conversation
            // that is silence, so the turn loop reads the reply rather than trusting the absence of a
            // failure.
            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.Equal(ConversationSession.EmptyReplyReason, turn.Failure);
            Assert.Null(turn.InterruptedAfter);

            // The writers still ran, and the transcript holds what the caller heard.
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal(1, session.State.Read("greetingTurns")!.GetValue<long>());
            Assert.Equal(ConversationSession.FallbackReply, session.Transcript[^1].Text);
        }

        [Fact]
        public async Task AnEmptyReply_LeavesTheSessionReadyForTheNextTurn()
        {
            using SequencedChatClient reply = new("", "I am back.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult first = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            TurnResult second = await session.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            Assert.Equal(ConversationSession.FallbackReply, first.ReplyText);
            Assert.Equal("I am back.", second.ReplyText);
            Assert.Null(second.Failure);
            Assert.Equal(1, second.TurnIndex);
        }

        [Fact]
        public async Task Streaming_SpeaksTheFallbackWhenTheRunProducesNoText()
        {
            using LifecycleChatClient reply = new();
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // The host hears the fallback rather than nothing at all: R2 makes a quiet run an ordinary
            // successful one, and the spoken line leaves the seam the same way any reply does.
            Assert.Equal([ConversationSession.FallbackReply], updates.Select(update => update.Text));
            Assert.NotNull(session.LastTurn);
            Assert.Equal(ConversationSession.FallbackReply, session.LastTurn.ReplyText);
            Assert.Equal(ConversationSession.EmptyReplyReason, session.LastTurn.Failure);
            Assert.Equal("close", session.Stage);
        }

        // -------------------------------------------------------------------------------------------
        // Section 8.7, sixth row: the 4th consecutive tool failure.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task TheFourthConsecutiveToolFailure_EndsTheTurnAndNeverKillsTheConversation()
        {
            using LoopingToolCallingChatClient reply = new();
            using SequencedChatClient fill = new(StayingNull);
            ThrowingToolBuilder tools = new();
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // MaximumConsecutiveErrorsPerRequest is 3, so the 4th failure throws out of the run.
            Assert.Equal(4, tools.Calls);
            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.NotNull(turn.Failure);
            Assert.StartsWith(ConversationSession.ToolFailureReason, turn.Failure, StringComparison.Ordinal);
            Assert.Contains(ThrowingToolBuilder.Message, turn.Failure, StringComparison.Ordinal);

            // The writers ran in their fixed order, and the machine picked the stage of the next turn.
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal("greeting", turn.StageAfter);
            Assert.False(session.IsComplete);

            // The transcript holds the spoken line alone. A half-finished tool round would otherwise
            // leave a conversation with no result behind, and the next turn would send it.
            Assert.Equal(2, session.Transcript.Count);
            Assert.Equal(ConversationSession.FallbackReply, session.Transcript[^1].Text);
        }

        [Fact]
        public async Task ARealDeclaredToolThatCannotReachItsEndpoint_EndsTheTurnPerRowSixAndKeepsTheConversation()
        {
            // The same row, reached the way a shipped tool actually reaches it. ThrowingToolBuilder
            // throws straight at the framework; this goes through DeclaredTool, so the classification
            // AuditingFunctionInvokingChatClient applies is what lets the fault out. Before the split
            // every fault became a result and this budget could never fire at all.
            using LoopingToolCallingChatClient reply = new();
            using SequencedChatClient fill = new(StayingNull);
            UnreachableEndpointToolBuilder tools = new();
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // MaximumConsecutiveErrorsPerRequest is 3, so the 4th failure throws out of the run.
            Assert.Equal(4, tools.Calls);
            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.NotNull(turn.Failure);
            Assert.StartsWith(ConversationSession.ToolFailureReason, turn.Failure, StringComparison.Ordinal);
            Assert.Contains(UnreachableEndpointToolBuilder.Message, turn.Failure, StringComparison.Ordinal);

            // The writers ran in their fixed order, the machine picked the stage of the next turn, and
            // the conversation is still alive.
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal("greeting", turn.StageAfter);
            Assert.False(session.IsComplete);
        }

        [Fact]
        public async Task ARealDeclaredToolWhoseFaultTheModelCanAnswer_CompletesTheTurnNormally()
        {
            // The half that must not regress. The tool answers with an error result, the model reads it,
            // and nothing about the turn is a failure: no budget spent, no fallback, a real reply.
            using ToolCallingChatClient reply = new("That order is already closed.");
            using SequencedChatClient fill = new(StayingNull);
            RefusedRequestToolBuilder tools = new();
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            Assert.Equal(1, tools.Calls);
            Assert.Null(turn.Failure);
            Assert.Equal("That order is already closed.", turn.ReplyText);
            Assert.False(session.IsComplete);
        }

        [Fact]
        public async Task TheFourthConsecutiveToolFailure_LeavesTheSessionUnlocked()
        {
            using LoopingToolCallingChatClient reply = new();
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ToolYaml, reply, fill, new ThrowingToolBuilder().Create).Create();

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // The _running flag went back to zero, so the next turn starts rather than throwing.
            TurnResult second = await session.RunTurnAsync("try again", TestContext.Current.CancellationToken);

            Assert.Equal(1, second.TurnIndex);
            Assert.Equal(ConversationSession.FallbackReply, second.ReplyText);
        }

        [Fact]
        public async Task Streaming_EndsTheTurnWhenTheFourthConsecutiveToolFailureThrows()
        {
            using LoopingToolCallingChatClient reply = new();
            using SequencedChatClient fill = new(StayingNull);
            ThrowingToolBuilder tools = new();
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync(
                "where is my order", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // The enumeration ends rather than throwing, so the host never sees the fault.
            Assert.Equal(4, tools.Calls);
            Assert.NotNull(session.LastTurn);
            Assert.Equal(ConversationSession.FallbackReply, session.LastTurn.ReplyText);
            Assert.StartsWith(ConversationSession.ToolFailureReason, session.LastTurn.Failure!, StringComparison.Ordinal);
            Assert.Equal(1, session.State.TurnIndex);
        }

        // -------------------------------------------------------------------------------------------
        // Item 6a: barge-in. The record holds what the caller heard.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task AnInterruption_RecordsTheHeardTextAndTheDurationTheRelayReported()
        {
            using ScriptedChatClient reply = new("I can help with that,", " and here is the long part.")
            {
                GateAfterFirstFragment = true,
            };
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, TestContext.Current.CancellationToken);

            await using IAsyncEnumerator<ChatResponseUpdate> updates = session.RunTurnStreamingAsync("hi", linked.Token).GetAsyncEnumerator(linked.Token);
            Assert.True(await updates.MoveNextAsync());
            Assert.Equal("I can help with that,", updates.Current.Text);

            // Section 7.1 reports both values on the interrupt frame, at 1 ms. Nothing estimates either.
            Assert.True(session.Interrupt("I can help", TimeSpan.FromMilliseconds(740)));
            Assert.False(await updates.MoveNextAsync());

            Assert.NotNull(session.LastTurn);
            Assert.Equal("I can help", session.LastTurn.ReplyText);
            Assert.Equal(TimeSpan.FromMilliseconds(740), session.LastTurn.InterruptedAfter);
            Assert.Null(session.LastTurn.Failure);

            // The transcript holds the text the caller heard, not the text the model produced.
            Assert.Equal("I can help", session.Transcript[^1].Text);
            Assert.DoesNotContain(session.Transcript, message => Contains(message, "long part"));
        }

        [Fact]
        public async Task AnInterruption_RunsEveryWriterAndLeavesTheSessionReady()
        {
            using ScriptedChatClient reply = new("hello", " there.") { GateAfterFirstFragment = true };
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, TestContext.Current.CancellationToken);

            await using (IAsyncEnumerator<ChatResponseUpdate> updates = session.RunTurnStreamingAsync("hi", linked.Token).GetAsyncEnumerator(linked.Token))
            {
                Assert.True(await updates.MoveNextAsync());
                Assert.True(session.Interrupt("hel", TimeSpan.FromMilliseconds(120)));

                while (await updates.MoveNextAsync())
                {
                    // The stream ends on the interruption, so nothing arrives here.
                }
            }

            // The writers ran in their fixed order, and the counter read the stage the turn spoke in.
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal(1, session.State.Read("greetingTurns")!.GetValue<long>());
            Assert.Equal("greeting", session.Stage);

            // The session is not locked, so the caller who interrupted can speak next.
            reply.OpenGate();
            TurnResult next = await session.RunTurnAsync("go on", linked.Token);
            Assert.Equal(1, next.TurnIndex);
            Assert.Null(next.InterruptedAfter);
        }

        [Fact]
        public async Task AnInterruption_NeverCutsATurnThatDoesNotStream()
        {
            // One audibility rule for both run shapes, decided by the owner on 2026-08-18: a run that
            // has handed the host nothing cannot be the turn the caller was hearing. A turn that does
            // not stream hands the host nothing until it returns, so a barge-in during it amends the
            // turn that finished last — the caller was still hearing THAT reply — and the running turn
            // finishes undisturbed. It used to be the other way: the frame cut the running turn and
            // recorded heard text of a reply no host had ever received.
            using ScriptedChatClient reply = new("hello", " there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult first = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            Assert.Equal("hello there.", first.ReplyText);

            // The second turn parks inside its model call, so the frame lands while it runs.
            reply.GateAfterFirstFragment = true;
            Task<TurnResult> running = session.RunTurnAsync("go on", TestContext.Current.CancellationToken);
            Assert.True(session.Interrupt("hello", TimeSpan.FromMilliseconds(90)));

            // The frame amended the FIRST turn — the only reply the caller could have been hearing —
            // and the record is readable before the second turn even finishes.
            TurnResult? amended = session.LastTurn;
            Assert.NotNull(amended);
            Assert.Equal(0, amended.TurnIndex);
            Assert.Equal("hello", amended.ReplyText);
            Assert.Equal(TimeSpan.FromMilliseconds(90), amended.InterruptedAfter);

            reply.OpenGate();
            TurnResult second = await running;

            // The running turn was never cut: it spoke its whole reply, uninterrupted.
            Assert.Equal("hello there.", second.ReplyText);
            Assert.Null(second.InterruptedAfter);
            Assert.Null(second.Failure);
            Assert.Equal(2, session.State.TurnIndex);
        }

        [Fact]
        public void AnInterruptionWithNoRunningTurn_ReportsFalseAndChangesNothing()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            // A frame that arrives after the turn ended must not drop the conversation, so it answers false.
            Assert.False(session.Interrupt("nothing played", TimeSpan.FromMilliseconds(5)));

            Assert.Null(session.LastTurn);
            Assert.Equal(0, session.State.TurnIndex);
            Assert.Empty(session.Transcript);
        }

        // -------------------------------------------------------------------------------------------
        // Section 8.6: the update stream carries content only.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task Streaming_DropsEveryUpdateThatCarriesNoContent()
        {
            using LifecycleChatClient reply = new("hello", " there.");
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // AsAIAgent() yields 47 updates for 40 text fragments, and seven carry no content. The seam
            // filters them once, so no host writes the filter again.
            Assert.Equal(6, reply.Yielded);
            Assert.Equal(["hello", " there."], [.. updates.Select(update => update.Text)]);
            Assert.NotNull(session.LastTurn);
            Assert.Equal("hello there.", session.LastTurn.ReplyText);
        }

        [Fact]
        public async Task Streaming_KeepsTheUpdateThatCarriesAToolCall()
        {
            using ToolCallingChatClient reply = new("your order is on the way.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ToolYaml, reply, fill, new StubToolBuilder(/*lang=json,strict*/ """{ "status": "shipped" }""").Create).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync(
                "where is my order", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // A tool call and a tool result are content a host may show, so the filter keeps them.
            Assert.Contains(updates, update => update.Contents.OfType<FunctionCallContent>().Any());
            Assert.Contains(updates, update => update.Text.Length > 0);
            Assert.DoesNotContain(updates, update => update.Contents.Count == 0);
        }

        // -------------------------------------------------------------------------------------------
        // D4: the inbound port.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public async Task TheSession_SatisfiesTheInboundPort()
        {
            using SequencedChatClient reply = new("hello there.", "still here.");
            using SequencedChatClient fill = new(StayingNull);
            IConversationPort port = Assert.IsType<IConversationPort>(Build(PolicyYaml, reply, fill).Create("conversation-9"), exactMatch: false);

            TurnResult turn = await port.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // The relay shim and the SSE endpoint both drive this contract and reach past it for nothing.
            Assert.Equal("conversation-9", port.ConversationId);
            Assert.Equal("greeting", port.Stage);
            Assert.False(port.IsComplete);
            Assert.Same(turn, port.LastTurn);

            // A frame that lands after the turn ended is recorded, not ignored. The vendor paces the
            // audio, so the caller was still hearing this reply long after the model stopped producing
            // it, and the port amends the finished turn. See the remarks on ConversationSession.Interrupt.
            Assert.True(port.Interrupt("nothing played", TimeSpan.Zero));

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in port.RunTurnStreamingAsync("still there?", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            Assert.Equal("still here.", string.Concat(updates.Select(update => update.Text)));
        }

        // -------------------------------------------------------------------------------------------
        // The factory.
        // -------------------------------------------------------------------------------------------
        [Fact]
        public void TheFactory_MakesAConversationIdWhenTheHostGivesNone()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSessionFactory factory = Build(PolicyYaml, reply, fill);

            ConversationSession first = factory.Create();
            ConversationSession second = factory.Create();

            Assert.NotEmpty(first.ConversationId);
            Assert.NotEqual(first.ConversationId, second.ConversationId);
        }

        [Fact]
        public async Task TheFactory_GivesEachConversationItsOwnStateAndItsOwnMachine()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(SaidGoodbye, StayingNull);
            ConversationSessionFactory factory = Build(PolicyYaml, reply, fill);

            ConversationSession first = factory.Create("conversation-1");
            ConversationSession second = factory.Create("conversation-2");

            _ = await first.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

            // One machine belongs to one conversation. The first conversation ended, and the second one has not started.
            Assert.True(first.IsComplete);
            Assert.False(second.IsComplete);
            Assert.Equal("greeting", second.Stage);
            Assert.Empty(second.Transcript);
            Assert.NotSame(first.State, second.State);

            // Both conversations share the one compiled agent. T44 and rule 16.
            Assert.Same(first.Compiled, second.Compiled);
        }

        [Fact]
        public void TheFactory_BuildsNoExtractorWhenTheDocumentDeclaresNone()
        {
            using SequencedChatClient reply = new("hello there.");
            CompiledAgent compiled = Compile(TwoStagesYaml, reply, null, null);

            Assert.Null(ConversationSessionFactory.CreateExtractor(compiled, new FakeChatClientFactory(reply)));
        }

        [Fact]
        public void TheFactory_BuildsTheExtractorTheDocumentDeclares()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            CompiledAgent compiled = Compile(PolicyYaml, reply, fill, null);

            StateExtractor? extractor = ConversationSessionFactory.CreateExtractor(
                compiled,
                new RoutingChatClientFactory(reply).Route("fill", fill));

            Assert.NotNull(extractor);
            Assert.Equal(["callerSaidGoodbye"], extractor.SlotNames);
        }

        // -------------------------------------------------------------------------------------------
        // Helpers.
        // -------------------------------------------------------------------------------------------
        private static bool Contains(ChatMessage message, string text)
        {
            return message.Text.Contains(text, StringComparison.Ordinal);
        }

        private static ConversationSessionFactory Build(
            string yaml,
            IChatClient reply,
            IChatClient? fill,
            Func<ToolConfiguration, AITool?>? tools = null,
            TimeProvider? timeProvider = null)
        {
            CompiledAgent compiled = Compile(yaml, reply, fill, tools);
            RoutingChatClientFactory chatClients = new(reply);
            if (fill is not null)
            {
                _ = chatClients.Route("fill", fill);
            }

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients),
                timeProvider);
        }

        private static CompiledAgent Compile(string yaml, IChatClient reply, IChatClient? fill, Func<ToolConfiguration, AITool?>? tools)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(reply);
            if (fill is not null)
            {
                _ = chatClients.Route("fill", fill);
            }

            return ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
                })["main"];
        }
    }
}
