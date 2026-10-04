using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.Voice.Ports
{
    /// <summary>
    /// Names the vendor behind one <c>providers.conversation</c> value: who carries the conversation.
    /// </summary>
    public interface IConversationAdapter : IVendorAdapter
    {
        /// <summary>
        /// Gets whether this transport's frames already carry text.
        /// </summary>
        bool CarriesText { get; }

        /// <summary>Gets what this vendor does itself. AgentCore's startup checks read it.</summary>
        CallTraits Traits => CallTraits.None;
    }
}
