using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.AgentCoreChatHistoryProviderTestSupport;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// Pins what the provider adds to <see cref="CallTranscript"/>: the lock, the write chain, and the
/// rule that a store failure never reaches the turn.
/// </summary>
public sealed class AgentCoreChatHistoryProviderTests
{
    [Fact]
    public async Task ProvideChatHistory_AfterAppend_ReturnsMessagesInOrder()
    {
        // Arrange
        var (provider, _, session) = await NewCall();
        AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");
        AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday");

        // Act
        var history = await ProvideAsync(provider, session);

        // Assert
        Assert.Equal(
            ["hello", "hi there", "order 41?", "it ships Friday"],
            history.Select(message => message.Text));
    }

    [Fact]
    public async Task AppendTurn_MultipleTurns_OrdinalsAreDenseAndUnique()
    {
        // Arrange
        var (provider, store, session) = await NewCall();
        AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");

        // Act
        AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal([0, 1, 2, 3], store.Rows.Select(row => row.Ordinal));
        Assert.Equal([0, 0, 1, 1], store.Rows.Select(row => row.TurnIndex));
        Assert.All(store.Rows, row => Assert.Equal(CallId, row.CallId));
    }

    [Fact]
    public async Task AppendTurn_RefusedTurn_IsStored()
    {
        // Arrange
        var (provider, store, session) = await NewCall();

        // Act
        AppendTurn(provider, session, turnIndex: 0, "something flagged", "I can't help with that.");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal(
            ["something flagged", "I can't help with that."],
            store.Rows.Select(row => row.Content.Text));
    }

    [Fact]
    public async Task AppendTurn_FailedTurn_IsStored()
    {
        // Arrange
        var (provider, store, session) = await NewCall();

        // Act
        AppendTurn(provider, session, turnIndex: 0, "check my order", "Sorry, I had trouble with that.");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal(
            ["check my order", "Sorry, I had trouble with that."],
            store.Rows.Select(row => row.Content.Text));
    }

    /// <summary>
    /// The framework offers to store a finished run, and this provider declines. Measured on
    /// Microsoft.Agents.AI 1.17.0, that hook stores the request verbatim — reminder and all — and is
    /// never called at all for a run the caller cut short, which is every barge-in. CallSession
    /// writes the turn it shaped instead.
    /// </summary>
    [Fact]
    public async Task StoreChatHistory_FinishedRun_StoresNothing()
    {
        // Arrange
        var (provider, store, session) = await NewCall();
        provider.BeginTurn(session, turnIndex: 0);

        // Act
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        await provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(
                StubAgent.Instance,
                session,
                [new ChatMessage(ChatRole.User, "<system-reminder>ask for the id</system-reminder>\norder 41?")],
                [new ChatMessage(ChatRole.Assistant, "it ships Friday")]),
            TestContext.Current.CancellationToken);
#pragma warning restore MAAI001

        // Assert
        await provider.DrainAsync(session);
        Assert.Empty(store.Rows);
        Assert.Empty(await ProvideAsync(provider, session));
    }

    [Fact]
    public async Task AppendTurn_ConcurrentAppends_LosesNoMessage()
    {
        // Arrange
        var (provider, store, session) = await NewCall();
        provider.BeginTurn(session, turnIndex: 0);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = Enumerable.Range(0, 20)
            .Select(index => Task.Run(
                async () =>
                {
                    await start.Task;
                    provider.AppendTurn(
                        session,
                        [
                            new ChatMessage(ChatRole.User, $"said {index}"),
                            new ChatMessage(ChatRole.Assistant, $"replied {index}"),
                        ]);
                },
                TestContext.Current.CancellationToken))
            .ToArray();

        // Act
        start.SetResult();
        await Task.WhenAll(turns);

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal(40, store.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 40), store.Rows.Select(row => row.Ordinal).Order());
    }

    [Fact]
    public async Task ProvideChatHistory_TwoConcurrentSessions_DoNotMix()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new RecordingCallStore());
        var first = new StubSession();
        var second = new StubSession();
        provider.BeginCall(first, "call-a", []);
        provider.BeginCall(second, "call-b", []);
        AppendTurn(provider, first, turnIndex: 0, "a said", "a heard");

        // Act
        AppendTurn(provider, second, turnIndex: 0, "b said", "b heard");

        // Assert
        Assert.Equal(["a said", "a heard"], (await ProvideAsync(provider, first)).Select(message => message.Text));
        Assert.Equal(["b said", "b heard"], (await ProvideAsync(provider, second)).Select(message => message.Text));
    }

    /// <summary>
    /// The framework validates this set at agent construction and again on every run, and refuses a
    /// collision. The value is MAF's default — the provider's own type name — and nothing files
    /// under it anymore, but a change still renames what the collision checks compare, so it stays
    /// pinned rather than inlined.
    /// </summary>
    [Fact]
    public void StateKeys_IsTheSingleProviderTypeNameKey()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider();

        // Assert
        Assert.Equal([StateKey], provider.StateKeys);
    }

    [Fact]
    public async Task AppendTurn_LeavesTheSessionStateBagEmpty()
    {
        // Arrange
        var (provider, _, session) = await NewCall();

        // Act
        AppendTurn(provider, session, turnIndex: 0, "order 41?", "it ships Friday");

        // Assert
        Assert.Equal(0, session.StateBag.Count);
        Assert.Equal(["order 41?", "it ships Friday"], provider.Read(session).Select(message => message.Text));
    }

    /// <summary>
    /// The provider is one object shared by every call, so two sessions must still reach two
    /// transcripts. State held against the provider rather than the session would merge them.
    /// </summary>
    [Fact]
    public void AppendTurn_TwoSessions_EachHoldsItsOwnTranscript()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new RecordingCallStore());
        var first = new StubSession();
        var second = new StubSession();
        provider.BeginCall(first, "call-a", []);
        provider.BeginCall(second, "call-b", []);

        // Act
        AppendTurn(provider, first, turnIndex: 0, "a said", "a heard");
        AppendTurn(provider, second, turnIndex: 0, "b said", "b heard");

        // Assert
        Assert.Equal(["a said", "a heard"], provider.Read(first).Select(message => message.Text));
        Assert.Equal(["b said", "b heard"], provider.Read(second).Select(message => message.Text));
    }

    [Fact]
    public async Task AppendTurn_BackingStoreThrows_DoesNotFailTheTurn()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new ThrowingCallStore());
        var session = new StubSession();
        provider.BeginCall(session, CallId, []);

        // Act
        AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal(["hello", "hi there"], (await ProvideAsync(provider, session)).Select(message => message.Text));
    }

    [Fact]
    public async Task BeginCall_BackingStoreThrows_TellsTheReporterWhichTurnWasLost()
    {
        // Arrange
        var provider = new AgentCoreChatHistoryProvider(new ThrowingCallStore());
        var session = new StubSession();
        List<int> dropped = [];
        provider.BeginCall(session, CallId, [], (turnIndex, _) => dropped.Add(turnIndex));

        // Act
        AppendTurn(provider, session, turnIndex: 3, "hello", "hi there");

        // Assert
        await provider.DrainAsync(session);
        Assert.Equal([3], dropped);
    }
}
