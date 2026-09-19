using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Sessions;

/// <summary>
/// What the relay socket asks of <see cref="IConversationSessions"/> over the life of one conversation.
/// </summary>
/// <remarks>
/// The unit tests of the store itself live in AgentCore.Application.Tests beside the store. This
/// file holds only what needs a real socket to prove.
/// </remarks>
public sealed class ConversationSessionsRelayTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30_000)]
    public async Task ATurnTellsTheStoreItsConversationIsStillBeingHad()
    {
        // The relay opens its session once and holds it for the whole conversation, so nothing else reads
        // it back. A read is the one sign a store gets that a conversation is still live, so without one
        // the idle sweep drops a long call out from under the turn about to run.
        using FragmentingChatClient reply = new("your order ships Friday");
        var factory = Factory(TelnyxRelayTurnTests.PolicyYaml, reply);
        CountingConversationSessions sessions = new(factory);

        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            configure: options => options.UseConversationSessions((_, _) => sessions));

        harness.Socket.Queue(RelayFrames.Setup(conversationSessionId: "conversation-live"));
        harness.Socket.Queue(RelayFrames.Prompt("when does my order ship?", last: true));

        for (var attempt = 0; attempt < 400 && sessions.Reads == 0; attempt++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(sessions.Reads > 0, "the turn never told the store its conversation is still being had.");
    }

    private static ConversationSessionFactory Factory(string yaml, IChatClient reply)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        RoutingChatClientFactory chatClients = new(reply);
        var compiled = ConfigurationCompiler.CompileAll(document, new AgentCompilationContext(chatClients))["main"];

        return new ConversationSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            ConversationSessionFactory.CreateExtractor(compiled, chatClients));
    }

    private sealed class CountingConversationSessions(IConversationSessionFactory factory) : IConversationSessions
    {
        private readonly InMemoryConversationSessions _inner =
            new(factory, InMemoryConversationSessions.DefaultIdleTimeout, TimeProvider.System);

        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public ValueTask<ConversationSession> OpenAsync(string? conversationId, CancellationToken cancellationToken = default)
            => _inner.OpenAsync(conversationId, cancellationToken);

        public ValueTask<ConversationSession?> TryGetAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return _inner.TryGetAsync(conversationId, cancellationToken);
        }

        public ValueTask CloseAsync(string conversationId, CancellationToken cancellationToken = default)
            => _inner.CloseAsync(conversationId, cancellationToken);
    }
}
