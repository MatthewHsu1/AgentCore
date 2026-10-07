using AgentCore.Application.Llm;
using AgentCore.Application.Ports;

namespace AgentCore.TestSupport
{
    /// <summary>
    /// A catalog that holds a map of provider and model pairs to their entry, and never leaves the
    /// process to answer.
    /// </summary>
    public sealed class MapModelCatalogPort : IModelCatalogPort
    {
        private readonly Dictionary<(string Provider, string Model), ModelCatalogEntry> _values = [];

        /// <summary>Adds one provider and model pair's entry, and returns this catalog.</summary>
        public MapModelCatalogPort With(string provider, string model, int contextWindow)
        {
            _values[(provider, model)] = new ModelCatalogEntry(contextWindow);
            return this;
        }

        /// <inheritdoc />
        public ValueTask<ModelCatalogEntry?> LookupAsync(
            string provider, string model, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                _values.TryGetValue((provider, model), out ModelCatalogEntry? entry) ? entry : null);
        }
    }
}
