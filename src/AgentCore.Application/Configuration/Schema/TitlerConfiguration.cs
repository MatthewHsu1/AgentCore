namespace AgentCore.Application.Configuration.Schema
{
    /// <summary>
    /// The conversation titler. It names a conversation from the newest messages of the conversation itself.
    /// </summary>
    public sealed record TitlerConfiguration
    {
        /// <summary>Gets the model the titler conversations.</summary>
        public required ModelReference Model { get; init; }
    }
}
