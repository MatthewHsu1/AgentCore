namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The call titler. It names a call from the first few messages of the call itself.
/// </summary>
/// <remarks>
/// The titler never speaks to the caller and writes nothing but the call's name. The block is
/// optional: a document that declares none leaves the titler on whichever <c>providers.llm</c>
/// entry the factory defaults to.
/// </remarks>
public sealed record TitlerConfiguration
{
    /// <summary>Gets the model the titler calls.</summary>
    public required ModelReference Model { get; init; }
}
