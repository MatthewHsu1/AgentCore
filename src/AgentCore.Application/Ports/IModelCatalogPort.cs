using AgentCore.Application.Llm;

namespace AgentCore.Application.Ports
{
    /// <summary>
    /// Looks up one vendor's models by name: whether the name exists, and its context window.
    /// </summary>
    public interface IModelCatalogPort
    {
        /// <summary>Looks up one model under one provider.</summary>
        /// <param name="provider">The catalog's provider key, such as <c>opencode-go</c>.</param>
        /// <param name="model">The model id, exactly as the vendor's own API names it.</param>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>The entry, or <see langword="null"/> when the provider knows no such model.</returns>
        ValueTask<ModelCatalogEntry?> LookupAsync(
            string provider, string model, CancellationToken cancellationToken = default);
    }
}
