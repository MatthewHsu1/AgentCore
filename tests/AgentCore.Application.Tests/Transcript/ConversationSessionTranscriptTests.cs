using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// Pins the conversation's move onto one <c>AgentSession</c>: what the run is sent, and what the message store keeps.
    /// </summary>
    public sealed class ConversationSessionTranscriptTests
    {
        private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        agents:
          # These tests read the exact messages the model sees; the clock line would be one more.
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

        private const string ToolYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        agents:
          items:
            - { id: only, instructions: "quote the price", tools: [ price_lookup ] }
        entries:
          main:
            agent: only
        """;

        private const string SlotYaml = """
        apiVersion: agentcore/v1
        state:
          orderId:
            type: string
            writer: extractor
            description: the order the caller is asking about
        guards:
          known: { var: orderId }
        agents:
          items:
            - { id: only, instructions: "ask for the order id" }
        entries:
          main:
            policy:
              initial: ask
              stages:
                - id: ask
                  agent: only
                  to: [ { stage: done, when: known } ]
                - id: done
                  agent: only
                  terminal: true
        """;

        private const string ToolResult = /*lang=json,strict*/ """{ "price": 50 }""";

        /// <summary>
        /// The record holds the words the caller heard, and never the tail the model
        /// produced. It is the message store that must hold them, not only the live history.
        /// </summary>
        [Fact]
        public async Task Interrupt_MidReply_StoredTranscriptHoldsHeardTextOnly()
        {
            RecordingConversationStore store = new();
            using ScriptedChatClient reply = new("Hello", " there", " caller") { GateAfterFirstFragment = true };
            ConversationSession session = CreateSession(OneAgentYaml, reply, store);
            (Task? turn, Task? spoke) = StartGatedTurn(session, "hi");
            await spoke;

            bool recorded = session.Cut(0, new TurnCut("Hello", TimeSpan.FromMilliseconds(300)));

            reply.OpenGate();
            await turn;
            Assert.True(recorded);
            await session.FlushTranscriptAsync();
            Assert.Equal(["hi", "Hello"], store.Live(session.ConversationId).Select(row => row.Content.Text));
        }

        /// <summary>
        /// The vendor paces the audio, so the model finishes streaming long before the caller finishes
        /// hearing, and the frame lands after the turn ended. The turn is then corrected in place — every
        /// word of it, not only its last message. A line the model wrote beside the tool call it
        /// announced is a line the caller may never have heard.
        /// </summary>
        [Fact]
        public async Task InterruptAfterTheTurnEnded_ToolTurnWithProse_StoresTheHeardWordsOnce()
        {
            RecordingConversationStore store = new();
            using ProseThenReplyChatClient reply = new("the price is fifty");
            ConversationSession session = CreateSession(ToolYaml, reply, store, new StubToolBuilder(ToolResult).Create);
            await DrainAsync(session.RunTurnStreamingAsync("how much?", TestContext.Current.CancellationToken));

            bool recorded = session.Cut(0, new TurnCut("the price", TimeSpan.FromMilliseconds(400)));

            Assert.True(recorded);
            await session.FlushTranscriptAsync();
            IReadOnlyList<ConversationMessage> rows = store.Live(session.ConversationId);
            Assert.DoesNotContain(
                rows.SelectMany(row => row.Content.Contents).OfType<TextContent>(),
                text => text.Text.Contains(ProseThenReplyChatClient.Prose, StringComparison.Ordinal));

            // The side effect ran, so the pair stays visible to the next turn.
            Assert.Contains(rows, row => row.Content.Contents.OfType<FunctionCallContent>().Any());
            Assert.Contains(rows, row => row.Content.Contents.OfType<FunctionResultContent>().Any());
            Assert.Equal(
                ["how much?", "the price"],
                rows.Select(row => row.Content.Text).Where(text => text.Length > 0));
        }

        /// <summary>
        /// A cut that reached back a turn would replace a sentence the
        /// caller heard in full, and nothing would detect it. The guard is <c>ConversationSession</c>'s, so this
        /// drives it through <see cref="ConversationSession.Cut"/> rather than through the provider.
        /// </summary>
        [Fact]
        public async Task Interrupt_AfterASecondTurn_LeavesTheFirstTurnsReplyWhole()
        {
            RecordingConversationStore store = new();
            RequestRecordingChatClient reply = new("hi there caller", "it ships Friday from the depot");
            ConversationSession session = CreateSession(OneAgentYaml, reply, store);
            _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync("order 41?", TestContext.Current.CancellationToken);

            bool recorded = session.Cut(1, new TurnCut("it ships", TimeSpan.FromMilliseconds(500)));

            Assert.True(recorded);
            await session.FlushTranscriptAsync();
            Assert.Equal(
                ["hello", "hi there caller", "order 41?", "it ships"],
                store.Live(session.ConversationId).Select(row => row.Content.Text));
        }

        [Fact]
        public async Task RunTurn_SecondTurn_SendsTheNewCallerMessageAloneAndTheModelStillSeesTheConversation()
        {
            RequestRecordingChatClient reply = new("hi there", "it ships Friday");
            ConversationSession session = CreateSession(OneAgentYaml, reply, new RecordingConversationStore());
            _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            _ = await session.RunTurnAsync("order 41?", TestContext.Current.CancellationToken);

            Assert.Equal(
                ["user:hello", "assistant:hi there", "user:order 41?"],
                reply.Requests[1]);
        }

        /// <summary>
        /// The reminder rides exactly one invocation, as instructions the framework merges and stores
        /// nowhere. Nothing of it reaches the caller's own message, so the message store keeps what was said.
        /// </summary>
        [Fact]
        public async Task RunTurn_WithAnUnfilledSlot_KeepsTheReminderOutOfTheStoredTranscript()
        {
            RecordingConversationStore store = new();
            RequestRecordingChatClient reply = new("which order?");
            ConversationSession session = CreateSession(SlotYaml, reply, store);

            _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            // The reminder rides a message of its own, below the transcript, so the instructions block
            // stays byte-identical across turns and the vendor's cacheable prefix covers the transcript.
            Assert.DoesNotContain("<system-reminder>", reply.Instructions[0] ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains(reply.Requests[0], message => message.Contains("<system-reminder>", StringComparison.Ordinal));
            await session.FlushTranscriptAsync();
            Assert.Equal(["hello", "which order?"], store.Live(session.ConversationId).Select(row => row.Content.Text));
        }

        private static ConversationSession CreateSession(
            string yaml, IChatClient reply, IConversationStore store, Func<ToolConfiguration, AITool?>? tools = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            FakeChatClientFactory chatClients = new(reply);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    ConversationStore = store,
                    Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
                })["main"];

            ConversationSessionFactory factory = new(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null);

            return factory.Create();
        }

        /// <summary>Starts a streaming turn on a background task and says when the caller can hear it.</summary>
        /// <param name="session">The conversation to run the turn on.</param>
        /// <param name="userInput">What the caller said.</param>
        /// <returns>The running turn, and a task that completes at its first spoken update.</returns>
        private static (Task Turn, Task Spoke) StartGatedTurn(ConversationSession session, string userInput)
        {
            TaskCompletionSource spoke = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task turn = Task.Run(
                async () =>
                {
                    await foreach (ChatResponseUpdate? update in session
                        .RunTurnStreamingAsync(userInput, TestContext.Current.CancellationToken)
                        .ConfigureAwait(false))
                    {
                        _ = spoke.TrySetResult();
                    }
                },
                CancellationToken.None);

            return (turn, spoke.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        /// <summary>Runs a streaming turn to its end. No fact here reads an update.</summary>
        private static async Task DrainAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
        {
            await foreach (ChatResponseUpdate? _ in updates.ConfigureAwait(false))
            {
            }
        }

        /// <summary>Writes a line beside the tool call it announces, then answers once the result lands.</summary>
        private sealed class ProseThenReplyChatClient(string reply) : IChatClient
        {
            /// <summary>The line the model speaks before it calls the tool.</summary>
            public const string Prose = "Let me check that for you";

            private const string ToolCallId = "conversation_1";

            private readonly string _reply = reply;

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                await Task.Yield();

                bool answered = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());
                string responseId = Guid.NewGuid().ToString("N");

                if (!answered && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [
                            new TextContent(Prose),
                              new FunctionCallContent(
                                  ToolCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal)),
                        ])
                    {
                        ResponseId = responseId,
                        MessageId = responseId,
                    };

                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false))
                {
                    updates.Add(update);
                }

                return updates.ToChatResponse();
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
    }
}
