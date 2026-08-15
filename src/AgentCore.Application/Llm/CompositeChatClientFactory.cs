using System.Collections.Concurrent;
using System.Globalization;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Llm;

/// <summary>
/// The one <see cref="IChatClientFactory"/> a config-driven host binds. It routes each
/// <c>providers.llm[]</c> entry to the <see cref="IChatClientAdapter"/> whose kind matches.
/// </summary>
/// <remarks>
/// <para>
/// The compile table asks for the client behind an <c>as</c> name and never reads a vendor name.
/// This class holds the vendor-neutral half of that mapping: the <c>as</c> map, the default entry,
/// the temperature wrapper, and the client cache. The vendor half lives in the adapters, one for
/// each <c>kind</c>, so the document alone decides which vendor answers which reference.
/// </para>
/// <para>
/// Every client is built while <see cref="CreateAsync"/> runs. A <c>kind</c> no adapter serves and a
/// credential that does not resolve both stop the host at startup, and not on the first call. An
/// agent that reached the telephone and then found no model is the silent failure the startup checks
/// exist to stop.
/// </para>
/// <para>
/// One vendor client is built for each <c>as</c> name and then shared, because a chat client is
/// thread-safe and a call costs a connection pool. A reference that also sets
/// <see cref="ModelReference.Temperature"/> takes a thin wrapper over that same vendor client, so the
/// document keeps its setting and the pool stays one.
/// </para>
/// </remarks>
public sealed class CompositeChatClientFactory : IChatClientFactory, IDisposable
{
    private readonly Dictionary<string, LlmProviderConfiguration> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IChatClient> _vendor = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IChatClient> _shaped = new(StringComparer.Ordinal);
    private LlmProviderConfiguration? _default;
    private int _disposed;

    private CompositeChatClientFactory()
    {
    }

    /// <summary>Builds the factory: one client for each <c>providers.llm[]</c> entry, now.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="secrets">The chain each adapter resolves its credential through, or <see langword="null"/>.</param>
    /// <param name="adapters">The adapters the host registers, one for each vendor it supports.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The factory.</returns>
    /// <exception cref="ConfigurationLoadException">
    /// One entry names a <c>kind</c> no adapter serves, or two entries answer to the same <c>as</c>
    /// name.
    /// </exception>
    public static async ValueTask<CompositeChatClientFactory> CreateAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort? secrets,
        IReadOnlyList<IChatClientAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(adapters);

        CompositeChatClientFactory factory = new();

        var llm = configuration.Providers?.Llm ?? EquatableList<LlmProviderConfiguration>.Empty;
        for (var index = 0; index < llm.Count; index++)
        {
            var entry = llm[index];
            var pointer = ConfigurationError.AppendPointer("/providers/llm", index);

            var seam = new VendorSeam(
                $"providers.llm entry '{entry.As}'",
                ConfigurationError.AppendPointer(pointer, "kind"),
                "options.UseChatClients(...)");
            var adapter = VendorAdapterSelector.Select(entry.Kind, adapters, seam);

            if (!factory._entries.TryAdd(entry.As, entry))
            {
                throw Fail(
                    ConfigurationError.AppendPointer(pointer, "as"),
                    $"two entries answer to the name '{entry.As}', so a model reference names two models.");
            }

            factory._vendor[entry.As] = await adapter
                .CreateClientAsync(entry, secrets, cancellationToken)
                .ConfigureAwait(false);

            factory._default ??= entry;
        }

        return factory;
    }

    /// <summary>Gets the client one model reference names.</summary>
    /// <param name="model">The reference, or <see langword="null"/> for the first declared entry.</param>
    /// <returns>The client. The caller never disposes it, because this factory owns it.</returns>
    /// <exception cref="ConfigurationLoadException">
    /// The reference names an <c>as</c> value <c>providers.llm</c> does not declare, or the document
    /// declares no model at all.
    /// </exception>
    public IChatClient GetChatClient(ModelReference? model)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var entry = Resolve(model);
        if (model?.Temperature is not { } temperature)
        {
            return _vendor[entry.As];
        }

        // The vendor client stays one for each 'as' name. Only the call settings differ, so the
        // wrapper sits above the shared client rather than beside it.
        var key = entry.As + "|" + temperature.ToString("R", CultureInfo.InvariantCulture);
        return _shaped.GetOrAdd(key, _ => new TemperatureChatClient(_vendor[entry.As], (float)temperature));
    }

    /// <summary>Releases every client the adapters built and every wrapper above them.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var client in _shaped.Values)
        {
            client.Dispose();
        }

        foreach (var client in _vendor.Values)
        {
            client.Dispose();
        }

        _shaped.Clear();
        _vendor.Clear();
    }

    /// <summary>Finds the entry one reference points at.</summary>
    /// <param name="model">The reference, or <see langword="null"/>.</param>
    /// <returns>The entry.</returns>
    private LlmProviderConfiguration Resolve(ModelReference? model)
    {
        if (model is null)
        {
            // An agent that inherits nothing takes the first declared model, which is the one the
            // document writes for the path that runs most.
            return _default ?? throw Fail(
                "/providers/llm",
                "the document declares no model, so nothing answers a reference that names none.");
        }

        return _entries.TryGetValue(model.Ref, out var entry)
            ? entry
            : throw Fail(
                "/providers/llm",
                $"the reference '{model.Ref}' names no entry of providers.llm. The document declares "
                + $"{Declared()}.");
    }

    /// <summary>Writes the declared names, so a failure names what the document does hold.</summary>
    /// <returns>The names, or a phrase for an empty section.</returns>
    private string Declared()
        => _entries.Count == 0 ? "no model" : string.Join(", ", _entries.Keys.Select(name => "'" + name + "'"));

    /// <summary>Builds the one exception every failure of this factory uses.</summary>
    /// <param name="pointer">The JSON Pointer into the document.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The exception.</returns>
    private static ConfigurationLoadException Fail(string pointer, string message)
        => new(new ConfigurationError
        {
            Pointer = pointer,
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });

    /// <summary>The wrapper one temperature takes, above the one shared vendor client.</summary>
    private sealed class TemperatureChatClient : DelegatingChatClient
    {
        private readonly float _temperature;

        public TemperatureChatClient(IChatClient inner, float temperature)
            : base(inner)
            => _temperature = temperature;

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => base.GetResponseAsync(messages, Shape(options), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => base.GetStreamingResponseAsync(messages, Shape(options), cancellationToken);

        // The document sets the default and a caller that sets its own keeps it, exactly as
        // ConfigureOptions did when the OpenAI factory owned this wrapper.
        private ChatOptions Shape(ChatOptions? options)
        {
            var shaped = options?.Clone() ?? new ChatOptions();
            shaped.Temperature ??= _temperature;
            return shaped;
        }
    }
}
