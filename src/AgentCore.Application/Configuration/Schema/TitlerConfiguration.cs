namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The conversation titler. It names a conversation from the first few messages of the conversation itself.
/// </summary>
/// <remarks>
/// The titler never speaks to the caller and writes nothing but the conversation's name. The block is
/// optional: a document that declares none leaves the titler on whichever <c>providers.llm</c>
/// entry the factory defaults to.
/// </remarks>
public sealed record TitlerConfiguration
{
    /// <summary>Gets the model the titler conversations.</summary>
    public required ModelReference Model { get; init; }
}
