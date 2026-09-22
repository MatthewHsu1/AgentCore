using System.Text.Json.Nodes;
using AgentCore.Application.Llm;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Infrastructure.Llm.ModelsDev
{
    /// <summary>
    /// Looks up models through the models.dev catalog, the open database OpenCode itself reads.
    /// </summary>
    public sealed class ModelsDevCatalogPort : IModelCatalogPort
    {
        /// <summary>The name this port opens its client under, on the pipeline of the host.</summary>
        public const string HttpClientName = "agentcore.models-dev";

        /// <summary>The configuration key a host names to override <see cref="DefaultCatalogEndpoint"/>.</summary>
        public const string CatalogEndpointConfigurationKey = "AgentCore:ModelsDev:CatalogEndpoint";

        /// <summary>The endpoint this port reads, unless a host names its own. No key, no documented rate limit.</summary>
        public static readonly Uri DefaultCatalogEndpoint = new("https://models.dev/api.json", UriKind.Absolute);

        private readonly HttpClient _client;

        private readonly Uri _catalogEndpoint;

        private JsonNode? _catalog;

        /// <summary>Creates the port over the outbound pipeline of the host.</summary>
        /// <param name="client">The client this port reads the catalog on. The port never disposes it.</param>
        /// <param name="catalogEndpoint">
        /// The endpoint to read. Defaults to <see cref="DefaultCatalogEndpoint"/> when omitted.
        /// </param>
        public ModelsDevCatalogPort(HttpClient client, Uri? catalogEndpoint = null)
        {
            ArgumentNullException.ThrowIfNull(client);

            _client = client;
            _catalogEndpoint = catalogEndpoint ?? DefaultCatalogEndpoint;
        }

        /// <summary>Builds the port over the outbound pipeline of the host.</summary>
        /// <param name="httpClients">The pipeline. This port asks it for <see cref="HttpClientName"/>.</param>
        /// <param name="catalogEndpoint">
        /// The endpoint to read. Defaults to <see cref="DefaultCatalogEndpoint"/> when omitted.
        /// </param>
        /// <returns>The port.</returns>
        public static ModelsDevCatalogPort Create(IHttpClientFactory httpClients, Uri? catalogEndpoint = null)
        {
            ArgumentNullException.ThrowIfNull(httpClients);

            return new ModelsDevCatalogPort(httpClients.CreateClient(HttpClientName), catalogEndpoint);
        }

        /// <summary>Builds the port, reading <see cref="CatalogEndpointConfigurationKey"/> off the host's own configuration.</summary>
        /// <param name="httpClients">The pipeline. This port asks it for <see cref="HttpClientName"/>.</param>
        /// <param name="configuration">The host's own configuration.</param>
        /// <returns>The port.</returns>
        public static ModelsDevCatalogPort CreateFromConfiguration(IHttpClientFactory httpClients, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            Uri? catalogEndpoint = configuration[CatalogEndpointConfigurationKey] is { } value
                ? new Uri(value, UriKind.Absolute)
                : null;

            return Create(httpClients, catalogEndpoint);
        }

        /// <inheritdoc />
        public async ValueTask<ModelCatalogEntry?> LookupAsync(
            string provider, string model, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(provider);
            ArgumentException.ThrowIfNullOrEmpty(model);

            JsonNode catalog = await LoadAsync(cancellationToken).ConfigureAwait(false);

            JsonNode? context = catalog[provider]?["models"]?[model]?["limit"]?["context"];
            return context?.GetValue<int>() is { } tokens ? new ModelCatalogEntry(tokens) : null;
        }

        /// <summary>Fetches the catalog once, and keeps every later call on the cached copy.</summary>
        private async ValueTask<JsonNode> LoadAsync(CancellationToken cancellationToken)
        {
            if (_catalog is { } cached)
            {
                return cached;
            }

            using HttpResponseMessage response = await _client
                .GetAsync(_catalogEndpoint, cancellationToken)
                .ConfigureAwait(false);

            _ = response.EnsureSuccessStatusCode();

            await using Stream body = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            _catalog = await JsonNode.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("models.dev answered an empty body.");

            return _catalog;
        }
    }
}
