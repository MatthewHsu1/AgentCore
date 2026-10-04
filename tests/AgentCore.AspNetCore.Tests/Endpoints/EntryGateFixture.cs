using System.Text;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>The two-entry host the entry gate tests share: a model per agent and a hook that decides inline.</summary>
    internal static class EntryGateFixture
    {
        internal const string Route = "/v1/chat/responses";

        internal const string TwoEntryYaml =
            """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: front, instructions: "answer visitors", model: { ref: front } }
                - { id: desk, instructions: "answer staff", model: { ref: desk } }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: front }
                - { kind: openai, model: gpt-4.1-mini, as: desk }
            entries:
              main:
                agent: front
              staff:
                agent: desk
            """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        internal static Task<ResponsesHost> StartAsync(Models models, Entry hook)
        {
            return ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                options =>
                {
                    models.Bind(options);
                    _ = options.UseHooks(hook);
                },
                map: app => app.MapResponses(Route));
        }

        internal static Task<HttpResponseMessage> PostAsync(ResponsesHost host)
        {
            HttpRequestMessage request = new(HttpMethod.Post, Route)
            {
                Content = new StringContent(/*lang=json,strict*/ """{ "stream": false, "input": "hi" }""", Encoding.UTF8, "application/json"),
            };

            return host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        }

        /// <summary>One model per agent, so a test reads which agent answered.</summary>
        internal sealed class Models
        {
            public FragmentingChatClient Front { get; } = new("front reply");

            public FragmentingChatClient Desk { get; } = new("desk reply");

            public void Bind(AgentCoreOptions options)
            {
                _ = options.UseChatClients(_ => new RoutingChatClientFactory().Route("front", Front).Route("desk", Desk));
            }
        }

        internal sealed class Entry(Action<EntryGate> decide) : AgentHook
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public override ValueTask BeforeEntryAsync(EntryGate gate, CancellationToken cancellationToken)
            {
                _ = Interlocked.Increment(ref _calls);
                decide(gate);
                return default;
            }
        }
    }
}
