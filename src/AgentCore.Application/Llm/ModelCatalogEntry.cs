namespace AgentCore.Application.Llm
{
    /// <summary>One model's facts, as <see cref="Ports.IModelCatalogPort"/> reports them.</summary>
    /// <param name="ContextWindow">The context window, in tokens.</param>
    public sealed record ModelCatalogEntry(int ContextWindow);
}
