using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>An output port that records what it was told to say, and when it was stopped.</summary>
internal sealed class FakeCallOutput : ICallOutputPort
{
    private readonly List<string> _spoken = [];
    private readonly Lock _gate = new();

    /// <summary>Gets every fragment this port was given, in order.</summary>
    public IReadOnlyList<string> Spoken
    {
        get { lock (_gate) { return [.. _spoken]; } }
    }

    /// <summary>Gets how many replies were closed.</summary>
    public int Completions { get; private set; }

    /// <summary>Gets how many times a barge-in stopped this port.</summary>
    public int Stops { get; private set; }

    /// <summary>Gets how many times this port was disposed.</summary>
    public int Disposals { get; private set; }

    public ValueTask SpeakAsync(string fragment, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _spoken.Add(fragment);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        Completions++;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Stops++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}
