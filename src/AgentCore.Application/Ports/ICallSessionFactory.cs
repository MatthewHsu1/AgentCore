using AgentCore.Application.Runtime;

namespace AgentCore.Application.Ports;

/// <summary>
/// Creates one session for one call.
/// </summary>
/// <remarks>
/// The compiled agent is a process singleton, and the state of one call is not. This is the seam
/// between the two: the host resolves this factory once and asks it for a session each time a call
/// arrives.
/// </remarks>
public interface ICallSessionFactory
{
    /// <summary>Creates the session of one call.</summary>
    /// <param name="callId">
    /// The id the host gives the call, or <see langword="null"/> to let the factory make one.
    /// </param>
    /// <returns>The session, in the initial stage, with no turn run yet.</returns>
    CallSession Create(string? callId = null);
}
