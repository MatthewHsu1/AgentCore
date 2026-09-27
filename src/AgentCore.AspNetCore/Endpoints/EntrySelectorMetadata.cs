namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>Endpoint metadata that names the <see cref="IEntrySelector"/> a route picks its entry with.</summary>
    /// <param name="SelectorType">The selector type, resolved from the request's services.</param>
    internal sealed record EntrySelectorMetadata(Type SelectorType);
}
