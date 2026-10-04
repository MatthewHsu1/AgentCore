using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>The span listeners and factory builders that <see cref="LibraryOpenTelemetryTests"/> shares across its tests.</summary>
    internal static class LibraryOpenTelemetryHarness
    {
        /// <summary>The default source name <c>OpenTelemetryChatClient</c> (Microsoft.Extensions.AI 10.8.3)
        /// resolves to when a caller names none. Read out of the restored assembly; see
        /// <c>ConfigurationCompiler.WithToolFailureAuditing</c>.</summary>
        internal const string ChatSourceName = "Experimental.Microsoft.Extensions.AI";

        /// <summary>The default source name <c>OpenTelemetryAgent</c> (Microsoft.Agents.AI 1.17.0) resolves
        /// to when a caller names none. Read out of the restored assembly; see
        /// <c>ConfigurationCompiler.BuildAgents</c>.</summary>
        internal const string AgentSourceName = "Experimental.Microsoft.Agents.AI";

        /// <summary>
        /// Whether one span is a per-round model call and not the relabeled invoke_agent span.
        /// </summary>
        internal static bool IsChatRoundSpan(Activity span)
        {
            return span.OperationName.StartsWith("chat", StringComparison.Ordinal)
                    && !span.DisplayName.StartsWith("invoke_agent", StringComparison.Ordinal);
        }

        /// <summary>Subscribes to the two library sources this task turns on.</summary>
        internal static ActivityListener ListenToLibrarySources(List<Activity> spans)
        {
            return ListenTo(spans, ChatSourceName, AgentSourceName);
        }

        /// <summary>Subscribes to the named sources, collecting every span each one closes.</summary>
        internal static ActivityListener ListenTo(List<Activity> spans, params string[] sources)
        {
            ActivityListener listener = new()
            {
                ShouldListenTo = source => Array.Exists(sources, name => string.Equals(source.Name, name, StringComparison.Ordinal)),
                Sample = (ref _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (spans)
                    {
                        spans.Add(activity);
                    }
                },
            };

            ActivitySource.AddActivityListener(listener);
            return listener;
        }

        /// <summary>Copies what the listener has collected so far. See the sibling helper in
        /// <c>TurnObservabilityTests</c> for why a reader must not enumerate the live list.</summary>
        internal static List<Activity> Snapshot(List<Activity> spans)
        {
            lock (spans)
            {
                return [.. spans];
            }
        }

        internal static ConversationSessionFactory Build(string yaml, IChatClient client, Func<ToolConfiguration, AITool?>? tools)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            FakeChatClientFactory factory = new(client);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(factory)
                {
                    Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
                })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, factory),
                timeProvider: null,
                logger: null,
                hooks: BuiltInHooks.Create(new InMemoryAuditSink()));
        }

        /// <summary>Builds a factory for a document that declares an extractor, scripting the two models
        /// apart on the <c>ref</c> names the document uses.</summary>
        internal static ConversationSessionFactory BuildWithExtractor(string yaml, IChatClient reply, IChatClient fill)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory factory = new RoutingChatClientFactory(reply).Route("fill", fill);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(document, new AgentCompilationContext(factory))["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, factory),
                timeProvider: null,
                logger: null,
                hooks: BuiltInHooks.Create(new InMemoryAuditSink()));
        }
    }
}
