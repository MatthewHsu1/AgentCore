using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>The session faults that the store and the extractor cause.</summary>
    public sealed class SessionFaultNoticeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // Values copied from ConversationSessionStateRestoreTests.ASecondSessionOfOneConversation_DropsASlotTheDocumentNoLongerDeclares.
        [Fact]
        public async Task AStoredSlotTheDocumentNoLongerDeclaresIsAPartialRestore()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("C-drift", Ct);
            _ = await store.AppendAsync(
                "C-drift",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState
                {
                    Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["retired"] = JsonValue.Create("gone") },
                },
                Ct);
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("sure");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], store, conversationId: "C-drift");

            _ = await session.RunTurnAsync("still there?", Ct);
            await session.FlushNoticesAsync();

            Fault fault = Assert.Single(hook.Of<Fault>());
            Assert.Equal((FaultKind.StateRestorePartial, (int?)null), (fault.Kind, fault.Scope.TurnIndex));
            Assert.Equal("the document no longer declares the slot 'retired'.", fault.Message);
        }

        // The extractor's own reason for a reply that is not JSON (StateExtractor).
        [Fact]
        public async Task AnExtractorReplyThatIsNotJsonIsAFailedExtraction()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            using ScriptedChatClient extractor = new("not json");
            RoutingChatClientFactory clients = new RoutingChatClientFactory(reply).Route("fill", extractor);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(InterruptionSessions.ExtractorYaml),
                new AgentCompilationContext(clients) { Hooks = [hook] })["main"];
            ConversationSession session = new ConversationSessionFactory(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), ConversationSessionFactory.CreateExtractor(compiled, clients)).Create();

            _ = await session.RunTurnAsync("I am Ann", Ct);
            await session.FlushNoticesAsync();

            Fault fault = Assert.Single(hook.Of<Fault>());
            Assert.Equal((FaultKind.ExtractionFailed, (int?)0), (fault.Kind, fault.Scope.TurnIndex));
            Assert.StartsWith("the extractor reply is not well-formed JSON", fault.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AStoreThatCannotPutTheBusyMarkIsAFailedMarkAndTheTurnStillRuns()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            MarkAnswers store = new(new InMemoryConversationStore(), () => throw new IOException("the store is down"));
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], store);

            TurnResult turn = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal("hello", turn.ReplyText);
            Fault fault = Assert.Single(hook.Of<Fault>());
            Assert.Equal((FaultKind.BusyMarkFailed, (int?)null), (fault.Kind, fault.Scope.TurnIndex));
            Assert.Equal("IOException: the store is down", fault.Message);
            _ = Assert.IsType<IOException>(fault.Cause);
        }

        [Fact(Timeout = 30_000)]
        public async Task AMarkAnotherSessionTookWhileTheTurnRanIsALostMark()
        {
            Fault fault = await RenewAsync(() => false);

            Assert.Equal((FaultKind.BusyMarkLost, (int?)null), (fault.Kind, fault.Scope.TurnIndex));
            Assert.Null(fault.Cause);
        }

        [Fact(Timeout = 30_000)]
        public async Task ARenewalTheStoreFailsIsAFailedMark()
        {
            Fault fault = await RenewAsync(() => throw new IOException("the store is down"));

            Assert.Equal((FaultKind.BusyMarkFailed, (int?)null), (fault.Kind, fault.Scope.TurnIndex));
            _ = Assert.IsType<IOException>(fault.Cause);
        }

        /// <summary>Takes the mark, then answers its first renewal, made while a turn is still streaming.</summary>
        private static async Task<Fault> RenewAsync(Func<bool> renewal)
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("Hello", " there") { GateAfterFirstFragment = true };
            MarkAnswers store = new(new InMemoryConversationStore(time), () => true, renewal);
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], store, time: time);
            await using IAsyncEnumerator<ChatResponseUpdate> stream = session.RunTurnStreamingAsync("hi", Ct).GetAsyncEnumerator(Ct);
            Assert.True(await stream.MoveNextAsync());

            time.Advance(ConversationBusyMark.Lease / 3);
            Fault fault = await hook.WaitForAsync<Fault>().WaitAsync(TimeSpan.FromSeconds(10), Ct);

            reply.OpenGate();
            while (await stream.MoveNextAsync())
            {
            }

            return fault;
        }

        /// <summary>Answers the busy-mark calls in order, then passes them to the store.</summary>
        private sealed class MarkAnswers(IConversationStore inner, params Func<bool>[] answers) : DelegatingConversationStore(inner)
        {
            private readonly Queue<Func<bool>> _answers = new(answers);

            public override ValueTask<bool> TryMarkBusyAsync(
                string conversationId, string holder, TimeSpan lease, CancellationToken cancellationToken = default)
            {
                return _answers.TryDequeue(out Func<bool>? answer)
                    ? ValueTask.FromResult(answer())
                    : base.TryMarkBusyAsync(conversationId, holder, lease, cancellationToken);
            }
        }
    }
}
