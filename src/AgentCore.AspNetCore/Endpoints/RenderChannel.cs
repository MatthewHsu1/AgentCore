using System.Collections.Concurrent;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>One thing the caller should see, and the name that decides how the browser draws it.</summary>
/// <param name="Name">The renderer the browser looks up.</param>
/// <param name="Data">The payload the renderer reads.</param>
internal sealed record RenderPayload(string Name, object Data);

/// <summary>
/// Carries what a call wants drawn from the tool that produced it to the stream that writes it.
/// </summary>
internal sealed class RenderChannel
{
    private readonly ConcurrentQueue<RenderPayload> _pending = new();

    /// <summary>Queues one thing to draw and answers the model with a receipt.</summary>
    /// <param name="name">The renderer the browser looks up.</param>
    /// <param name="data">The payload. It goes to the browser and nowhere else.</param>
    /// <returns>The one line the model sees.</returns>
    public string Publish(string name, object data)
    {
        _pending.Enqueue(new RenderPayload(name, data));

        return $"the caller can now see the {name}.";
    }

    /// <summary>Takes everything queued so far.</summary>
    /// <returns>The payloads, oldest first. It is empty when nothing is waiting.</returns>
    public IReadOnlyList<RenderPayload> Drain()
    {
        List<RenderPayload> taken = [];

        while (_pending.TryDequeue(out var payload))
        {
            taken.Add(payload);
        }

        return taken;
    }
}
