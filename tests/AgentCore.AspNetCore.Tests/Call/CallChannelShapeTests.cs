using AgentCore.Application.Call;
using AgentCore.AspNetCore.Call;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Call;

/// <summary>
/// The arbiter serves a split channel exactly as it serves a bundled one.
/// </summary>
/// <remarks>
/// If this file ever needs a Telnyx type to compile, D8 has failed and the seam is in the wrong
/// place. That is the whole assertion.
/// </remarks>
public sealed class CallChannelShapeTests
{
    [Fact(Timeout = 30_000)]
    public async Task ASplitChannelOfTwoObjects_RunsAScriptedCallThroughTheSameArbiter()
    {
        var session = new ScriptedConversationPort();
        var output = new FakeCallOutput();
        var input = new FakeCallInput();
        var channel = new CallChannel(input, output);

        var arbiter = new CallTurnArbiter(
            session,
            channel.Output,
            new ConnectionTaskObserver(() => session.CallId, (_, _) => { }, (_, _, _) => { }, (_, _, _) => false),
            _ => { },
            _ => { },
            TestContext.Current.CancellationToken);

        // The scripted call holds every turn on a gate until a test opens it, so this turn is let
        // through before it starts: this test is about the shape of the channel, and never about
        // the held-prompt window the arbiter's own tests use that gate for.
        session.ReleaseTurn();

        await arbiter.StartTurnAsync("hello");
        await arbiter.CurrentTurn;

        Assert.NotEmpty(output.Spoken);
        Assert.Equal(1, output.Completions);

        await channel.DisposeAsync();
        Assert.Equal(1, input.Disposals);
        Assert.Equal(1, output.Disposals);
    }
}
