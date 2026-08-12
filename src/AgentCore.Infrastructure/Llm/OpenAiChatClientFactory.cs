using System.ClientModel;
using System.Collections.Concurrent;
using System.Globalization;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentCore.Infrastructure.Llm;

/// <summary>
/// The OpenAI adapter behind <see cref="IChatClientFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// The compile table asks for the client behind an <c>as</c> name and never reads a vendor name. This
/// class holds the whole mapping: it finds the <c>providers.llm[]</c> entry whose <c>as</c> matches
/// the reference, reads the <c>kind</c> of that entry, and builds the client for its <c>model</c>.
/// The seam therefore moves to another vendor by replacing this one class.
/// </para>
/// <para>
/// A <c>kind</c> this adapter does not serve fails when the adapter is built, and not on the first
/// call. An agent that reached the telephone and then found no model is the silent failure the
/// startup checks exist to stop.
/// </para>
/// <para>
/// The API key never appears in this file. <see cref="CreateAsync"/> asks the
/// <see cref="ISecretResolverPort"/> chain for <see cref="ApiKeySecretName"/> first, then falls back
/// to the <see cref="ApiKeyVariableName"/> variable every OpenAI tool already reads. No message and
/// no exception of this class carries the value.
/// </para>
/// <para>
/// One vendor client is built for each <c>as</c> name and then shared, because a chat client is
/// thread-safe and a call costs a connection pool. A reference that also sets
/// <see cref="ModelReference.Temperature"/> takes a thin wrapper over that same vendor client, so the
/// document keeps its setting and the pool stays one.
/// </para>
/// </remarks>
public sealed class OpenAiChatClientFactory : IChatClientFactory, IDisposable
{
    /// <summary>The one <c>providers.llm[].kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "openai";

    /// <summary>The <c>${secret:name}</c> name the resolver chain is asked for.</summary>
    public const string ApiKeySecretName = "openai-api-key";

    /// <summary>The standard OpenAI environment variable, read when the chain holds no name.</summary>
    public const string ApiKeyVariableName = "OPENAI_API_KEY";

    private readonly Dictionary<string, LlmProviderConfiguration> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IChatClient> _vendor = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IChatClient> _shaped = new(StringComparer.Ordinal);
    private readonly OpenAIClient _client;
    private readonly LlmProviderConfiguration? _default;
    private int _disposed;

    /// <summary>Creates the adapter over the <c>providers.llm</c> section of one document.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="apiKey">The key the vendor client authenticates with.</param>
    /// <exception cref="ConfigurationLoadException">
    /// One <c>providers.llm[]</c> entry names a <c>kind</c> this adapter does not serve, or two
    /// entries answer to the same <c>as</c> name.
    /// </exception>
    public OpenAiChatClientFactory(AgentCoreConfiguration configuration, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        var llm = configuration.Providers?.Llm ?? EquatableList<LlmProviderConfiguration>.Empty;
        for (var index = 0; index < llm.Count; index++)
        {
            var entry = llm[index];
            var pointer = ConfigurationError.AppendPointer("/providers/llm", index);

            // The kind is a vendor name, and a vendor name is written by a human. It matches without
            // regard to case, and every other name in the document stays ordinal.
            if (!string.Equals(entry.Kind, ProviderKind, StringComparison.OrdinalIgnoreCase))
            {
                throw Fail(
                    ConfigurationError.AppendPointer(pointer, "kind"),
                    $"the entry '{entry.As}' is kind: {entry.Kind}, and this host binds the {ProviderKind} "
                    + "adapter only. Bind an adapter for that kind, or change the entry.");
            }

            if (!_entries.TryAdd(entry.As, entry))
            {
                throw Fail(
                    ConfigurationError.AppendPointer(pointer, "as"),
                    $"two entries answer to the name '{entry.As}', so a model reference names two models.");
            }

            _default ??= entry;
        }

        _client = new OpenAIClient(new ApiKeyCredential(apiKey));
    }

    /// <summary>Builds the adapter, reading the API key through the chain and then the environment.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="secrets">The resolver chain, or <see langword="null"/> to read the environment only.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The adapter.</returns>
    /// <exception cref="SecretResolutionException">Neither the chain nor the environment holds a key.</exception>
    /// <exception cref="ConfigurationLoadException">The document names a <c>kind</c> this adapter does not serve.</exception>
    public static async ValueTask<OpenAiChatClientFactory> CreateAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? key = null;
        if (secrets is not null)
        {
            key = await secrets.TryResolveAsync(ApiKeySecretName, cancellationToken).ConfigureAwait(false);
        }

        key ??= Environment.GetEnvironmentVariable(ApiKeyVariableName);
        if (key is not { Length: > 0 })
        {
            throw new SecretResolutionException(
                "the OpenAI API key did not resolve. Bind a resolver that holds '" + ApiKeySecretName
                + "', or set the " + ApiKeyVariableName + " variable. This adapter holds no key of its own.");
        }

        return new OpenAiChatClientFactory(configuration, key);
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
            return Vendor(entry);
        }

        // The vendor client stays one for each 'as' name. Only the call settings differ, so the
        // wrapper sits above the shared client rather than beside it.
        var key = entry.As + "|" + temperature.ToString("R", CultureInfo.InvariantCulture);
        return _shaped.GetOrAdd(
            key,
            _ => new ChatClientBuilder(Vendor(entry))
                .ConfigureOptions(options => options.Temperature ??= (float)temperature)
                .Build());
    }

    /// <summary>Releases every client this factory built.</summary>
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

    /// <summary>Builds the one vendor client of one <c>as</c> name, or reads the built one.</summary>
    /// <param name="entry">The provider entry.</param>
    /// <returns>The client.</returns>
    private IChatClient Vendor(LlmProviderConfiguration entry)
        => _vendor.GetOrAdd(entry.As, _ => _client.GetChatClient(entry.Model).AsIChatClient());

    /// <summary>Writes the declared names, so a failure names what the document does hold.</summary>
    /// <returns>The names, or a phrase for an empty section.</returns>
    private string Declared()
        => _entries.Count == 0 ? "no model" : string.Join(", ", _entries.Keys.Select(name => "'" + name + "'"));

    /// <summary>Builds the one exception every failure of this adapter uses.</summary>
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
