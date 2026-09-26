using System.Threading.Channels;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;
using AgentCore.AspNetCore.Voice;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>One <see cref="VoiceConversationLoop"/> over real conversation sessions and a fake model, fed by hand.</summary>
    internal sealed class VoiceLoopHarness
    {
        private readonly Channel<ConversationInput> _inputs = Channel.CreateUnbounded<ConversationInput>();

        private VoiceLoopHarness(IChatClient reply, IConversationOutputPort output)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(TelnyxRelayTurnTests.PolicyYaml);
            RoutingChatClientFactory chatClients = new(reply);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(document, new AgentCompilationContext(chatClients))[SingleEntrySessionFactories.MainEntry];
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor: null);

            Sessions = new InMemoryConversationSessions(
                SingleEntrySessionFactories.Of(factory),
                TimeSpan.FromMinutes(5),
                TimeProvider.System);
            Loop = new VoiceConversationLoop(
                Sessions,
                SingleEntrySessionFactories.MainEntry,
                output,
                new ConnectionTaskObserver(() => Loop?.ConversationId ?? "(none)", (_, _) => { }, (_, _, _) => { }, (_, _, _) => false),
                TimeProvider.System,
                NullLogger.Instance,
                TimeSpan.FromSeconds(5),
                CancellationToken.None,
                new VoiceOptions(UserAway: null, new Dictionary<string, FillerOptions>()));
        }

        /// <summary>Gets the sessions the loop opens conversations in.</summary>
        public InMemoryConversationSessions Sessions { get; }

        /// <summary>Gets the loop under test.</summary>
        public VoiceConversationLoop Loop { get; }

        /// <summary>Builds the loop, reading nothing yet.</summary>
        /// <param name="reply">The model behind every agent.</param>
        /// <param name="output">Where every reply goes.</param>
        /// <returns>The harness.</returns>
        public static VoiceLoopHarness Create(IChatClient reply, IConversationOutputPort output)
        {
            return new(reply, output);
        }

        /// <summary>Starts the loop reading from inputs the test sends by hand.</summary>
        /// <param name="reply">The model behind every agent.</param>
        /// <param name="output">Where every reply goes.</param>
        /// <returns>The harness, and the loop's own reading task.</returns>
        public static (VoiceLoopHarness Harness, Task Running) Start(IChatClient reply, IConversationOutputPort output)
        {
            VoiceLoopHarness harness = new(reply, output);
            return (harness, harness.Loop.RunAsync(harness._inputs.Reader.ReadAllAsync()));
        }

        /// <summary>Hands the loop one inbound event.</summary>
        public void Send(ConversationInput input)
        {
            _ = _inputs.Writer.TryWrite(input);
        }

        /// <summary>Ends the inbound stream, so the loop's reading task ends.</summary>
        public void Complete()
        {
            _ = _inputs.Writer.TryComplete();
        }
    }
}
