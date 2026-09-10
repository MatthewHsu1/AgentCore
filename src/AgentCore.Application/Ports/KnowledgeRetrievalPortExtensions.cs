namespace AgentCore.Application.Ports;

/// <summary>
/// Typed helpers over <see cref="IKnowledgeRetrievalPort.GetService"/>.
/// </summary>
public static class KnowledgeRetrievalPortExtensions
{
    /// <summary>Asks one store for a capability it may also serve.</summary>
    /// <typeparam name="TService">The capability being asked for.</typeparam>
    /// <param name="port">The store to ask.</param>
    /// <param name="serviceKey">Names one of several, when a store serves more than one.</param>
    /// <returns>The capability, or <see langword="null"/> when this store does not serve it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="port"/> is <see langword="null"/>.</exception>
    public static TService? GetService<TService>(this IKnowledgeRetrievalPort port, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(port);

        return port.GetService(typeof(TService), serviceKey) is TService service ? service : default;
    }
}
