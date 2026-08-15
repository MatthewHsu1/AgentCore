using AgentCore.Application.Speech;

namespace AgentCore.Application.Ports;

/// <summary>Opens the speech channel for one call.</summary>
public interface ISpeechChannelFactory
{
    /// <summary>Opens both halves of one call.</summary>
    /// <param name="context">What the call is.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>The pair. The caller disposes it when the call ends.</returns>
    ValueTask<SpeechChannel> OpenAsync(SpeechChannelContext context, CancellationToken cancellationToken = default);
}
