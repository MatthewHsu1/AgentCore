using System.Net.WebSockets;
using System.Text;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>The sideband over a real socket: the bearer key, a fragmented message, and the close.</summary>
    public sealed class WebSocketSidebandTests
    {
        [Fact(Timeout = 30_000)]
        public async Task AttachSendsTheKeyAndReadsAFragmentedMessageWhole()
        {
            string? authorization = null;
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
            _ = builder.Logging.ClearProviders();
            await using WebApplication app = builder.Build();
            _ = app.UseWebSockets();
            _ = app.Map("/v1/live/sessions/rtc_1/attach", async http =>
            {
                authorization = http.Request.Headers.Authorization;
                using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync();
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"session."), WebSocketMessageType.Text, endOfMessage: false, CancellationToken.None);
                await socket.SendAsync(Encoding.UTF8.GetBytes("closed\"}"), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            });
            await app.StartAsync(TestContext.Current.CancellationToken);
            Uri address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/");

            await using ILiveSideband sideband = await WebSocketSideband.ConnectAsync(new LiveAttach("rtc_1", "sk-test", address), TestContext.Current.CancellationToken);

            Assert.Equal("{\"type\":\"session.closed\"}", await sideband.ReceiveAsync(TestContext.Current.CancellationToken));
            Assert.Null(await sideband.ReceiveAsync(TestContext.Current.CancellationToken));
            Assert.Equal("Bearer sk-test", authorization);
        }
    }
}
