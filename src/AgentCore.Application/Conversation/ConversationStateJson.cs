using System.Text.Json;

namespace AgentCore.Application.Conversation
{
    /// <summary>How <see cref="ConversationSessionState"/> is encoded. Every store agrees on it.</summary>
    public static class ConversationStateJson
    {
        /// <summary>The shared options.</summary>
        public static JsonSerializerOptions Options { get; } = Build();

        private static JsonSerializerOptions Build()
        {
            JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

            options.MakeReadOnly(populateMissingResolver: true);

            return options;
        }
    }
}
