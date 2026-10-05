using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>What every entry of one document shares once compiled.</summary>
    /// <param name="Configuration">The loaded document.</param>
    /// <param name="Conversations">The store every conversation and every word of it is kept in.</param>
    /// <param name="History">The conversation history, over <paramref name="Conversations"/>.</param>
    /// <param name="Hooks">The hooks of this compile, shared by every entry.</param>
    internal sealed record CompiledDocument(
        AgentCoreConfiguration Configuration,
        IConversationStore Conversations,
        AgentCoreChatHistoryProvider History,
        HookRuntime Hooks);
}
