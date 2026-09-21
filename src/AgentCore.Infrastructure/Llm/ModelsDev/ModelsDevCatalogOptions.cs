namespace AgentCore.Infrastructure.Llm.ModelsDev
{
    /// <summary>Settings for <see cref="ModelsDevCatalogPort"/>.</summary>
    public sealed class ModelsDevCatalogOptions
    {
        /// <summary>The one endpoint this port reads. No key, no documented rate limit.</summary>
        public Uri CatalogEndpoint { get; set; } = new("https://models.dev/api.json", UriKind.Absolute);
    }
}
