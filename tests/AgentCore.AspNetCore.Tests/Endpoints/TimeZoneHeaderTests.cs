using System.Net;
using AgentCore.Application.Runtime.Harness;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The <c>X-AgentCore-Time-Zone</c> header: the browser's zone reaches the clock line the model
/// reads, and a missing or unknown zone leaves the server's own in place.
/// </summary>
public sealed class TimeZoneHeaderTests
{
    private const string OneAgentYaml =
        """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: solo, instructions: "answer" }
          entries:
            main:
              agent: solo
          providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
          """;

    // 2026-09-17 23:30 UTC: still Thursday in Chicago, already Friday morning in Taipei.
    private static readonly DateTimeOffset Instant = new(2026, 9, 17, 23, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task HeaderNamesAZone_TheClockLineReadsTheDateInIt()
    {
        using FragmentingChatClient reply = new("answer");
        FakeTimeProvider clock = new(Instant);
        await using var host = await ResponsesHost.StartAsync(OneAgentYaml, reply, options => options.TimeProvider = clock);

        using var response = await host.PostAsync(
            """{ "stream": false, "input": "what day is it" }""",
            new Dictionary<string, string> { [ResponsesEndpointRouteBuilderExtensions.TimeZoneHeaderName] = "Asia/Taipei" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            reply.LastRequest!,
            message => message.Role == ChatRole.System
                && message.Text == "Today is Friday, 2026-09-18. The local time is 07:30, Asia/Taipei (UTC+08:00).");
    }

    [Fact]
    public async Task HeaderNamesNoKnownZone_TheClockLineReadsTheServerZone()
    {
        using FragmentingChatClient reply = new("answer");
        FakeTimeProvider clock = new(Instant);
        await using var host = await ResponsesHost.StartAsync(OneAgentYaml, reply, options => options.TimeProvider = clock);

        using var response = await host.PostAsync(
            """{ "stream": false, "input": "what day is it" }""",
            new Dictionary<string, string> { [ResponsesEndpointRouteBuilderExtensions.TimeZoneHeaderName] = "Mars/Olympus_Mons" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = ClockContextProvider.Describe(clock, clock.LocalTimeZone);
        Assert.Contains(reply.LastRequest!, message => message.Role == ChatRole.System && message.Text == expected);
    }
}
