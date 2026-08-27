namespace AgentCore.Application.Ports;

/// <summary>
/// Carries one thing the caller should see from the tool that produced it to the host that shows it.
/// </summary>
public interface IRenderPort
{
    /// <summary>Sends one thing to be shown to the caller of the running call.</summary>
    void Publish(string name, string renderId, object data, bool transient = false);
}
