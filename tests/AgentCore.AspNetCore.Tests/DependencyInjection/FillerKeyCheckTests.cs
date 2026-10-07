using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection
{
    /// <summary>
    /// Owner ruling 2026-09-23: a filler key that names no served tool is not refused; startup logs one
    /// warning with the pointer to the key.
    /// </summary>
    public sealed class FillerKeyCheckTests
    {
        private const string Yaml =
            """
        apiVersion: agentcore/v1
        tools:
          - { id: look_it_up, kind: binding, binds: lookup }
        agents:
          items:
            - { id: solo, instructions: "answer", tools: [ look_it_up ] }
        entries:
          main:
            agent: solo
        providers:
          conversation:
            kind: telnyx-relay
            filler:
              look_it_up: { say: "One moment", delaySeconds: 1 }
              look_it_upp: { say: "One moment", delaySeconds: 1 }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
        """;

        private static readonly HashSet<string> Served = new(StringComparer.Ordinal) { "look_it_up" };

        [Fact]
        public void AnUnknownKeyLogsOneWarningWithItsPointer_AndAKnownKeyLogsNone()
        {
            using RecordingLoggerFactory logs = new();

            FillerKeyCheck.Warn(ConfigurationLoader.LoadYaml(Yaml), Served, logs.CreateLogger("boot"));

            CapturedLine line = Assert.Single(logs.Lines);
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Equal("/providers/conversation/filler/look_it_upp", line.Field<string>("Pointer"));
            Assert.Equal("look_it_upp", line.Field<string>("ToolId"));
        }
    }
}
