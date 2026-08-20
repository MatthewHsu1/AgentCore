using AgentCore.Application.Call;

namespace AgentCore.Application.Ports;

/// <summary>Opens the channel for one call.</summary>
public interface ICallChannelFactory
{
    /// <summary>Opens both halves of one call.</summary>
    /// <param name="context">What the call is.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>The pair. The caller disposes it when the call ends.</returns>
    ValueTask<CallChannel> OpenAsync(CallChannelContext context, CancellationToken cancellationToken = default);
}
