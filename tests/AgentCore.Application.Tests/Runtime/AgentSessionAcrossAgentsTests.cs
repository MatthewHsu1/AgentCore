using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// Pins the library fact the transcript paragraph of <see cref="Application.Runtime.CallSession"/>
/// stands on: one <see cref="AgentSession"/> carries a call that changes agent.
/// </summary>
/// <remarks>
/// <para>
/// The paragraph used to claim the opposite — "a conversation bound to one agent cannot carry a call
/// that changes agent" — and this test is the disproof, kept so a library upgrade that breaks the
/// corrected claim fails here instead of silently re-validating the old design reason. Each agent
/// holds its own <see cref="InMemoryChatHistoryProvider"/>; sharing the
/// <see cref="InMemoryChatHistoryProviderOptions.StateKey"/> is what makes both providers read and
/// write the same state in the one session's bag.
/// </para>
/// <para>
/// The session still owns the transcript, for the reasons the corrected paragraph gives: the
/// barge-in amendment and the failed-turn reshaping have no seam in a library history provider, and
/// the graph rows accept no foreign session. This test guards the FACT, not a design choice.
/// </para>
/// </remarks>
public sealed class AgentSessionAcrossAgentsTests
{
    private const string SharedKey = "agentcore.test.transcript";

    [Fact]
    public async Task OneSession_CarriesACall_AcrossTwoAgents()
    {
        var clientA = new EchoingChatClient("A");
        var clientB = new EchoingChatClient("B");

        ChatClientAgent agentA = new(clientA, new ChatClientAgentOptions
        {
            Name = "agent-a",
            ChatHistoryProvider = new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions { StateKey = SharedKey }),
        });
        ChatClientAgent agentB = new(clientB, new ChatClientAgentOptions
        {
            Name = "agent-b",
            ChatHistoryProvider = new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions { StateKey = SharedKey }),
        });

        // One session, created by agent A, then driven across both agents: A, B, A again.
        var session = await agentA.CreateSessionAsync(TestContext.Current.CancellationToken);

        _ = await agentA.RunAsync("turn one", session, cancellationToken: TestContext.Current.CancellationToken);
        _ = await agentB.RunAsync("turn two", session, cancellationToken: TestContext.Current.CancellationToken);
        _ = await agentA.RunAsync("turn three", session, cancellationToken: TestContext.Current.CancellationToken);

        // What each model actually received: the agent of turn 2 saw turn 1's whole exchange, and
        // the agent of turn 3 saw everything, including the turn the OTHER agent spoke.
        Assert.Equal(["user:turn one"], clientA.Requests[0]);
        Assert.Equal(
            ["user:turn one", "assistant:A answered turn 1", "user:turn two"],
            clientB.Requests[0]);
        Assert.Equal(
            [
                "user:turn one", "assistant:A answered turn 1",
                "user:turn two", "assistant:B answered turn 1",
                "user:turn three",
            ],
            clientA.Requests[1]);

        // The session's own record holds the one whole call, in order.
        Assert.True(session.TryGetInMemoryChatHistory(out var history, SharedKey));
        Assert.Equal(
            [
                "turn one", "A answered turn 1",
                "turn two", "B answered turn 1",
                "turn three", "A answered turn 2",
            ],
            history.Select(message => message.Text));
    }

    /// <summary>Answers every request with a text reply, and records what it was asked.</summary>
    private sealed class EchoingChatClient : IChatClient
    {
        private readonly string _name;

        public EchoingChatClient(string name) => _name = name;

        /// <summary>Gets every request this client answered, one role-prefixed line per message.</summary>
        public List<List<string>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            Requests.Add([.. messages.Select(message => $"{message.Role}:{message.Text}")]);
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, $"{_name} answered turn {Requests.Count}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This experiment only runs the non-streaming path.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
