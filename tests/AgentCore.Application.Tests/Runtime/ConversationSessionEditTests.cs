using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime.Clarification;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// A caller sends an earlier message again, and the conversation takes back what it said after it.
    /// </summary>
    public sealed class ConversationSessionEditTests
    {
        private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

        private const string TerminalYaml = """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: only, instructions: "answer the caller" }
          entries:
            main:
              policy:
                initial: done
                stages:
                  - { id: done, agent: only, terminal: true }
          """;

        [Fact]
        public async Task AnEdit_TakesTheReplacedWordsOutOfWhatTheModelReads()
        {
            using ScriptedChatClient scripted = new("an answer.");
            RequestCapturingChatClient reply = new(scripted);
            ConversationSession session = CreateSession(OneAgentYaml, reply);

            _ = await session.RunTurnAtOriginAsync(
                "first question",
                new ConversationTurnOrigin("caller-1", null) { NamesParent = true },
                TestContext.Current.CancellationToken);
            string? firstReply = session.LastReplyMessageId;

            _ = await session.RunTurnAtOriginAsync(
                "second question",
                new ConversationTurnOrigin("caller-2", firstReply) { NamesParent = true },
                TestContext.Current.CancellationToken);

            _ = await session.RunTurnAtOriginAsync(
                "second question, rewritten",
                new ConversationTurnOrigin("caller-3", firstReply) { NamesParent = true },
                TestContext.Current.CancellationToken);

            // What the model was actually handed, not what the reply says: a stub controls the reply
            // whatever the history did.
            List<string> seen = [.. reply.Requests[^1].Select(message => message.Text)];
            Assert.Contains("first question", seen);
            Assert.Contains("second question, rewritten", seen);
            Assert.DoesNotContain("second question", seen);
        }

        [Fact]
        public async Task AnEditOnATerminalConversation_IsRefusedAndTakesNothing()
        {
            using ScriptedChatClient reply = new("an answer.");
            ConversationSession session = CreateSession(TerminalYaml, reply);

            _ = await session.RunTurnAtOriginAsync(
                "q1",
                new ConversationTurnOrigin("caller-1", null) { NamesParent = true },
                TestContext.Current.CancellationToken);
            string? firstReply = session.LastReplyMessageId;
            Assert.True(session.IsComplete);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RunTurnAtOriginAsync(
                    "q1, rewritten",
                    new ConversationTurnOrigin("caller-2", null) { NamesParent = true },
                    TestContext.Current.CancellationToken));

            // The withdrawal deletes. A turn the guards refuse must not have taken the tail of the conversation
            // with it on the way out, or the caller is left with neither the old words nor the new.
            Assert.Equal(2, session.Transcript.Count);
            Assert.NotNull(firstReply);
        }

        [Fact]
        public async Task AnOriginThatNamesNoParent_WithdrawsNothing()
        {
            using ScriptedChatClient reply = new("an answer.");
            RecordingHook hook = new();
            ConversationSession session = CreateSession(OneAgentYaml, reply, hook);

            _ = await session.RunTurnAsync("q1", TestContext.Current.CancellationToken);

            // A null parent means the start of the conversation and would take every word of it. A caller that
            // named its own message and said nothing about a parent has asked for no such thing.
            _ = await session.RunTurnAtOriginAsync(
                "q2",
                new ConversationTurnOrigin("caller-2", null) { NamesParent = false },
                TestContext.Current.CancellationToken);

            Assert.Equal(4, session.Transcript.Count);
            await session.FlushNoticesAsync();
            Assert.Empty(hook.Of<TurnSuperseded>());
        }

        // Clarifications.Withdraw: an edit-and-resend takes back what was last named to the caller, and leaves
        // the ask counter alone.
        [Fact]
        public async Task AnEdit_TakesBackWhatWasLastNamedToTheCaller()
        {
            using ScriptedChatClient reply = new("an answer.");
            ConversationSession session = CreateSession(OneAgentYaml, reply);
            _ = await session.RunTurnAsync("q1", TestContext.Current.CancellationToken);
            string? firstReply = session.LastReplyMessageId;
            _ = await session.RunTurnAsync("q2", TestContext.Current.CancellationToken);
            session.Clarifications.Update("brand", slot =>
            {
                slot.LastNamed = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "ct900" });
                slot.ProbeAsks = 2;
            });

            _ = await session.RunTurnAtOriginAsync(
                "q2, rewritten",
                new ConversationTurnOrigin("caller-3", firstReply) { NamesParent = true },
                TestContext.Current.CancellationToken);

            Clarifications.SlotSnapshot after = session.Clarifications.Read("brand");
            Assert.Equal(Clarifications.LastNamed.None, after.LastNamed);
            Assert.Equal(2, after.ProbeAsks);
        }

        private static ConversationSession CreateSession(
            string yaml, IChatClient reply, AgentHook? hook = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            FakeChatClientFactory chatClients = new(reply);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    ConversationStore = new InMemoryConversationStore(),
                    Tools = TestToolRegistry.From(document, null, TestContext.Current.CancellationToken),
                })["main"];

            ConversationSessionFactory factory = new(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null,
                hooks: hook is null ? null : [hook]);

            return factory.Create("conversation-1");
        }
    }
}
