namespace AgentCore.Application.Transcript;

/// <summary>Mints the name a message takes when nobody names it.</summary>
public static class CallMessageIds
{
    /// <summary>Mints a new message id.</summary>
    public static string New() => Guid.CreateVersion7().ToString("n");
}
