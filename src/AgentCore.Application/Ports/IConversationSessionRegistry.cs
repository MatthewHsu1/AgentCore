namespace AgentCore.Application.Ports
{
    /// <summary>
    /// Finds the one session owner for the whole app, and the entries it serves.
    /// </summary>
    public interface IConversationSessionRegistry
    {
        /// <summary>Gets the names of the entries the document declares.</summary>
        IReadOnlyCollection<string> Entries { get; }

        /// <summary>Gets the one session owner over every entry.</summary>
        IConversationSessions Sessions { get; }
    }
}
