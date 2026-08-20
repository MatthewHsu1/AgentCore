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
/// What the relay socket asks of <see cref="ICallSessions"/> over the life of one call.
/// </summary>
/// <remarks>
/// The unit tests of the store itself live in AgentCore.Application.Tests beside the store. This
/// file holds only what needs a real socket to prove.
/// </remarks>
public sealed class CallSessionsRelayTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30_000)]
    public async Task ATurnTellsTheStoreItsCallIsStillBeingHad()
    {
        // The relay opens its session once and holds it for the whole call, so nothing else reads
        // it back. A read is the one sign a store gets that a call is still live, so without one
        // the idle sweep drops a long call out from under the turn about to run.
        using FragmentingChatClient reply = new("your order ships Friday");
        var factory = Factory(TelnyxRelayTurnTests.PolicyYaml, reply);
        CountingCallSessions sessions = new(factory);

        await using var harness = await RelayConnectionHarness.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            services: collection =>
            {
                collection.AddSingleton<ICallSessions>(sessions);
                collection.AddSingleton<ICallSessionFactory>(factory);
            });

        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-live"));
        harness.Socket.Queue(RelayFrames.Prompt("when does my order ship?", last: true));

        for (var attempt = 0; attempt < 400 && sessions.Reads == 0; attempt++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(sessions.Reads > 0, "the turn never told the store its call is still being had.");
    }

    private static CallSessionFactory Factory(string yaml, IChatClient reply)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        RoutingChatClientFactory chatClients = new(reply);
        var compiled = ConfigurationCompiler.Compile(document, new AgentCompilationContext(chatClients));

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients));
    }

    private sealed class CountingCallSessions(ICallSessionFactory factory) : ICallSessions
    {
        private readonly InMemoryCallSessions _inner =
            new(factory, InMemoryCallSessions.DefaultIdleTimeout, TimeProvider.System);

        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public ValueTask<CallSession> OpenAsync(string? callId, CancellationToken cancellationToken = default)
            => _inner.OpenAsync(callId, cancellationToken);

        public ValueTask<CallSession?> TryGetAsync(string callId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return _inner.TryGetAsync(callId, cancellationToken);
        }

        public ValueTask CloseAsync(string callId, CancellationToken cancellationToken = default)
            => _inner.CloseAsync(callId, cancellationToken);
    }
}
