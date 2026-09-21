using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>What every entry of one document shares once compiled.</summary>
    /// <param name="Configuration">The loaded document.</param>
    /// <param name="Conversations">The store every conversation and every word of it is kept in.</param>
    /// <param name="History">Store 1, over <paramref name="Conversations"/>.</param>
    internal sealed record CompiledDocument(
        AgentCoreConfiguration Configuration,
        IConversationStore Conversations,
        AgentCoreChatHistoryProvider History);
}
