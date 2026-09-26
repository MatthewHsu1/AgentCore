using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>One conversation of one agent over a scripted model, for the classes that test the Cut rule.</summary>
    internal static class ConversationSessionCutTestSupport
    {
        internal static (ConversationSession Session, RecordingConversationStore Store, InMemoryAuditSink Sink) Create(IChatClient reply)
        {
            RecordingConversationStore store = new();
            InMemoryAuditSink sink = new();
            return (Create(reply, store, sink), store, sink);
        }

        internal static ConversationSession Create(
            IChatClient reply, IConversationStore store, InMemoryAuditSink? sink = null, TimeProvider? time = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(ConversationSessionTestSupport.OneAgentYaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(new FakeChatClientFactory(reply)) { ConversationStore = store })["main"];

            ConversationSessionFactory factory = new(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null,
                timeProvider: time,
                observers: ConversationObservers.Standard(sink ?? new InMemoryAuditSink(), logger: null));

            return factory.Create();
        }

        /// <summary>Reads a started turn's reply to its end on the thread pool, so the turn runs while the test goes on.</summary>
        internal static Task ReadToEndAsync(TurnRun run, CancellationToken cancellationToken)
        {
            return Task.Run(
                async () =>
                {
                    await foreach (ChatResponseUpdate _ in run.Updates.WithCancellation(cancellationToken))
                    {
                    }
                },
                cancellationToken);
        }

        internal static List<string> Texts(IEnumerable<ChatMessage> messages)
        {
            return [.. messages.Where(message => message.Text.Length > 0).Select(message => message.Text)];
        }
    }
}
