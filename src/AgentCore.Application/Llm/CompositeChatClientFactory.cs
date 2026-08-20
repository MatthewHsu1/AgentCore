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
/// and the two client caches, vendor and shaped. The vendor half lives in the adapters, one for
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

        var llm = configuration.Providers?.Llm ?? [];
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
        // wrapper sits above the shared client rather than beside it. ConfigureOptions clones the
        // caller's ChatOptions (or starts a new one) and never mutates them, which is the same
        // semantic a hand-rolled wrapper used to reimplement.
        var key = entry.As + "|" + temperature.ToString("R", CultureInfo.InvariantCulture);
        return _shaped.GetOrAdd(
            key,
            _ => _vendor[entry.As]
                .AsBuilder()
                .ConfigureOptions(options => options.Temperature ??= (float)temperature)
                .Build());
    }

    /// <summary>Releases every client the adapters built.</summary>
    /// <remarks>
    /// A shaped client is a view over a shared vendor client, not an owner of one: it holds no
    /// resource of its own, and its own <see cref="IDisposable.Dispose"/> chains straight through
    /// to the vendor client it wraps (that is how <see cref="DelegatingChatClient"/> and the client
    /// <c>ConfigureOptions</c> builds both work). Disposing the shaped clients here as well as the
    /// vendor clients would therefore dispose a shared vendor client once for every shaped wrapper
    /// built over it, plus once more directly - safe only by accident, and wrong the moment a vendor
    /// client's <c>Dispose</c> is not idempotent. Only the vendor client is released; the shaped
    /// dictionary is dropped without disposing what it held.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
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
}
