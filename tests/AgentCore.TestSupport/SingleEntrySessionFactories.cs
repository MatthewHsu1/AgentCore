using AgentCore.Application.Ports;

namespace AgentCore.TestSupport
{
    /// <summary>The one entry name a single-factory test fixture opens under, and the map an owner takes for it.</summary>
    public static class SingleEntrySessionFactories
    {
        /// <summary>The entry every single-factory test fixture opens its conversations under.</summary>
        public const string MainEntry = "main";

        /// <summary>Wraps one factory as the single-entry map <see cref="IConversationSessions"/> owners take.</summary>
        public static IReadOnlyDictionary<string, IConversationSessionFactory> Of(IConversationSessionFactory factory)
        {
            return new Dictionary<string, IConversationSessionFactory>(StringComparer.Ordinal) { [MainEntry] = factory };
        }
    }
}
