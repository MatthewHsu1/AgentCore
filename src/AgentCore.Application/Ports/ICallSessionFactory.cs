using AgentCore.Application.Calls;
using AgentCore.Application.Runtime;

namespace AgentCore.Application.Ports;

/// <summary>
/// Creates one session for one call.
/// </summary>
public interface ICallSessionFactory
{
    /// <summary>Creates the session of one call.</summary>
    CallSession Create(string? callId = null, CallSessionState? state = null);
}
