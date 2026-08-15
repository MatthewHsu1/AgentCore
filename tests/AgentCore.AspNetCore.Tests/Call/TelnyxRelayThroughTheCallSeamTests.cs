using System.Net;
using AgentCore.AspNetCore.Call;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Call;

/// <summary>
/// The one place the shipped vendor and the vendor-neutral route meet in a running host.
/// </summary>
/// <remarks>
/// <para>
/// <c>CallRouteSelectionTests</c> proves the selection over fakes and names no vendor, which is
/// spec §12. <c>CallOptionsFromDocumentTests</c> proves
/// <c>TelnyxRelayCallAdapter.BuildOptions</c> over a document. Neither of them runs
/// <c>TelnyxRelayCallAdapter.Map</c>, and that method is the single executable line joining the
/// shipped vendor to the seam. This file is where it runs.
/// </para>
/// <para>
/// This file may name Telnyx freely: joining the real vendor to the real route is precisely what it
/// exists to prove, and a version of it written over a fake would prove nothing new.
/// </para>
/// <para>
/// It runs offline against a fake model. There is no Telnyx account, no network call, and no API
/// key here.
/// </para>
/// </remarks>
public sealed class TelnyxRelayThroughTheCallSeamTests
{
    [Fact(Timeout = 30_000)]
    public async Task APlainGetToTheVendorNeutralRoute_ReachesTheRelayHandler()
    {
        using SequencedChatClient reply = new("hello");

        // The host names no vendor at the route. providers.call: { kind: telnyx-relay } is what
        // picks this adapter, and app.MapCall() is what asks.
        await using var host = await TelnyxRelayHost.StartThroughCallSeamAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            options => options.UseCall(new TelnyxRelayCallAdapter()));

        var answer = await host.GetAsync(CallEndpointRouteBuilderExtensions.DefaultPattern);

        // 404 would mean MapCall mapped nothing, and every call to this deployment would be lost
        // with nothing to read. 400 is the relay's own HandleAsync refusing a request that is not a
        // WebSocket upgrade, so it proves both halves at once: the route exists, and the handler
        // behind it is the Telnyx one.
        Assert.NotEqual(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }
}
