using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// Pins the call's move onto one <c>AgentSession</c>: what the run is sent, and what store 1 keeps.
/// </summary>
public sealed class CallSessionTranscriptTests
{
    private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        name: transcript-check
        agents:
          items:
            - { id: only, instructions: "greet the caller" }
        """;

    private const string ToolYaml = """
        apiVersion: agentcore/v1
        name: transcript-tool-check
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read }
        agents:
          items:
            - { id: only, instructions: "quote the price", tools: [ price_lookup ] }
        """;

    private const string SlotYaml = """
        apiVersion: agentcore/v1
        name: reminder-check
        state:
          orderId:
            type: string
            writer: extractor
            description: the order the caller is asking about
        guards:
          known: { var: orderId }
        policy:
          initial: ask
          stages:
            - id: ask
              agent: only
              to: [ { stage: done, when: known } ]
            - id: done
              agent: only
              terminal: true
        agents:
          items:
            - { id: only, instructions: "ask for the order id" }
        """;

    private const string ToolResult = """{ "price": 50 }""";

    /// <summary>
    /// Item 6a and R4: the record holds the words the caller heard, and never the tail the model
    /// produced. It is store 1 that must hold them, not only the live history.
    /// </summary>
    [Fact]
    public async Task Interrupt_MidReply_StoredTranscriptHoldsHeardTextOnly()
    {
        // Arrange
        RecordingTranscriptStore store = new();
        using ScriptedChatClient reply = new("Hello", " there", " caller") { GateAfterFirstFragment = true };
        var session = CreateSession(OneAgentYaml, reply, store);
        var (turn, spoke) = StartGatedTurn(session, "hi");
        await spoke;

        // Act
        var recorded = session.Interrupt("Hello", TimeSpan.FromMilliseconds(300));

        // Assert
        reply.OpenGate();
        await turn;
        Assert.True(recorded);
        await session.FlushTranscriptAsync();
        Assert.Equal(["hi", "Hello"], store.Live(session.CallId).Select(row => row.Content.Text));
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
        // Arrange
        RecordingTranscriptStore store = new();
        using ProseThenReplyChatClient reply = new("the price is fifty");
        var session = CreateSession(ToolYaml, reply, store, new StubToolFactory(ToolResult));
        await DrainAsync(session.RunTurnStreamingAsync("how much?", TestContext.Current.CancellationToken));

        // Act
        var recorded = session.Interrupt("the price", TimeSpan.FromMilliseconds(400));

        // Assert
        Assert.True(recorded);
        await session.FlushTranscriptAsync();
        var rows = store.Live(session.CallId);
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
    /// Step 1's second failure mode: a cut that reached back a turn would replace a sentence the
    /// caller heard in full, and nothing would detect it. The guard is <c>CallSession</c>'s, so this
    /// drives it through <see cref="CallSession.Interrupt"/> rather than through the provider.
    /// </summary>
    [Fact]
    public async Task Interrupt_AfterASecondTurn_LeavesTheFirstTurnsReplyWhole()
    {
        // Arrange
        RecordingTranscriptStore store = new();
        RequestRecordingChatClient reply = new("hi there caller", "it ships Friday from the depot");
        var session = CreateSession(OneAgentYaml, reply, store);
        _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);
        _ = await session.RunTurnAsync("order 41?", TestContext.Current.CancellationToken);

        // Act
        var recorded = session.Interrupt("it ships", TimeSpan.FromMilliseconds(500));

        // Assert
        Assert.True(recorded);
        await session.FlushTranscriptAsync();
        Assert.Equal(
            ["hello", "hi there caller", "order 41?", "it ships"],
            store.Live(session.CallId).Select(row => row.Content.Text));
    }

    [Fact]
    public async Task RunTurn_SecondTurn_SendsTheNewCallerMessageAloneAndTheModelStillSeesTheCall()
    {
        // Arrange
        RequestRecordingChatClient reply = new("hi there", "it ships Friday");
        var session = CreateSession(OneAgentYaml, reply, new RecordingTranscriptStore());
        _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Act
        _ = await session.RunTurnAsync("order 41?", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            ["user:hello", "assistant:hi there", "user:order 41?"],
            reply.Requests[1]);
    }

    /// <summary>
    /// The reminder rides exactly one invocation, as instructions the framework merges and stores
    /// nowhere. Nothing of it reaches the caller's own message, so store 1 keeps what was said.
    /// </summary>
    [Fact]
    public async Task RunTurn_WithAnUnfilledSlot_KeepsTheReminderOutOfTheStoredTranscript()
    {
        // Arrange
        RecordingTranscriptStore store = new();
        RequestRecordingChatClient reply = new("which order?");
        var session = CreateSession(SlotYaml, reply, store);

        // Act
        _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Assert
        // The reminder rides a message of its own, below the transcript, so the instructions block
        // stays byte-identical across turns and the vendor's cacheable prefix covers the transcript.
        Assert.DoesNotContain("<system-reminder>", reply.Instructions[0] ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(reply.Requests[0], message => message.Contains("<system-reminder>", StringComparison.Ordinal));
        await session.FlushTranscriptAsync();
        Assert.Equal(["hello", "which order?"], store.Live(session.CallId).Select(row => row.Content.Text));
    }

    private static CallSession CreateSession(
        string yaml, IChatClient reply, ITranscriptStore store, IAgentToolFactory? tools = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new FakeChatClientFactory(reply);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { TranscriptStore = store, Tools = tools });

        var factory = new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null);

        return factory.Create();
    }

    /// <summary>Starts a streaming turn on a background task and says when the caller can hear it.</summary>
    /// <param name="session">The call to run the turn on.</param>
    /// <param name="userInput">What the caller said.</param>
    /// <returns>The running turn, and a task that completes at its first spoken update.</returns>
    /// <remarks>
    /// A run that has handed the host nothing is not the turn the caller is hearing, so a barge-in
    /// before the first update takes the amendment path instead and records nothing. Waiting for that
    /// update is what makes the cut land in the reply rather than after it.
    /// </remarks>
    private static (Task Turn, Task Spoke) StartGatedTurn(CallSession session, string userInput)
    {
        TaskCompletionSource spoke = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = Task.Run(
            async () =>
            {
                await foreach (var _ in session
                    .RunTurnStreamingAsync(userInput, TestContext.Current.CancellationToken)
                    .ConfigureAwait(false))
                {
                    spoke.TrySetResult();
                }
            },
            CancellationToken.None);

        return (turn, spoke.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Runs a streaming turn to its end. No fact here reads an update.</summary>
    private static async Task DrainAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        await foreach (var _ in updates.ConfigureAwait(false))
        {
        }
    }

    /// <summary>Writes a line beside the tool call it announces, then answers once the result lands.</summary>
    /// <remarks>
    /// This is what a real model produces, and it is the shape a cut has to survive: the prose and
    /// the call ride one message, so the words and the side effect cannot be dropped together.
    /// </remarks>
    private sealed class ProseThenReplyChatClient : IChatClient
    {
        /// <summary>The line the model speaks before it calls the tool.</summary>
        public const string Prose = "Let me check that for you";

        private const string ToolCallId = "call_1";

        private readonly string _reply;

        public ProseThenReplyChatClient(string reply) => _reply = reply;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var answered = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());
            var responseId = Guid.NewGuid().ToString("N");

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
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
