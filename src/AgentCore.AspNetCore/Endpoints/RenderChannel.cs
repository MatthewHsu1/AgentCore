using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>One thing the caller should see, and the name that decides how the browser draws it.</summary>
/// <param name="Name">The renderer the browser looks up.</param>
/// <param name="Data">The payload the renderer reads.</param>
internal sealed record RenderPayload(string Name, object Data);

/// <summary>
/// Carries what a call wants drawn from the tool that produced it to the stream that writes it.
/// </summary>
internal sealed class RenderChannel : IRenderPort
{
    private readonly ConcurrentQueue<RenderPayload> _pending = new();

    /// <summary>Queues one thing to draw.</summary>
    /// <param name="name">The renderer the browser looks up.</param>
    /// <param name="data">The payload. It goes to the browser and nowhere else.</param>
    public void Publish(string name, object data)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);

        _pending.Enqueue(new RenderPayload(name, data));
    }

    /// <summary>Takes the oldest payload waiting.</summary>
    /// <param name="payload">The payload, when one was waiting.</param>
    /// <returns><see langword="true"/> when one was taken.</returns>
    public bool TryTake([NotNullWhen(true)] out RenderPayload? payload)
        => _pending.TryDequeue(out payload);

    /// <summary>Throws away everything queued.</summary>
    public void Clear()
    {
        while (_pending.TryDequeue(out _))
        {
        }
    }
}
