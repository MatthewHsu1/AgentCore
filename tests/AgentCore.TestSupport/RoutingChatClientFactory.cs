using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// Hands one client to the reply model and another to the extractor model.
/// </summary>
/// <remarks>
/// <c>providers.llm[].as</c> names each model, and the document points <c>extractor.model</c> at one
/// of those names. This factory routes on that name, so a test scripts the two models apart.
/// </remarks>
public sealed class RoutingChatClientFactory : IChatClientFactory
{
    private readonly Dictionary<string, IChatClient> _byName = new(StringComparer.Ordinal);
    private readonly IChatClient? _fallback;

    /// <param name="fallback">
    /// The client for a reference with no route, or <see langword="null"/> to throw on one.
    /// </param>
    public RoutingChatClientFactory(IChatClient? fallback = null) => _fallback = fallback;

    /// <summary>Binds one client to one <c>as</c> name.</summary>
    public RoutingChatClientFactory Route(string name, IChatClient client)
    {
        _byName[name] = client;
        return this;
    }

    public IChatClient GetChatClient(ModelReference? model)
        => model is not null && _byName.TryGetValue(model.Ref, out var client)
            ? client
            : _fallback ?? throw new KeyNotFoundException($"No offline client is routed to the model '{model?.Ref}'.");

    public int? GetContextWindow(ModelReference? model) => 128_000;
}