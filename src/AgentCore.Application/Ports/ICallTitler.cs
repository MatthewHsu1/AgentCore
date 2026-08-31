namespace AgentCore.Application.Ports;

/// <summary>Makes a call's title from its words.</summary>
public interface ICallTitler
{
    /// <summary>Generates one call's title, streaming it as it arrives.</summary>
    /// <param name="callId">The call to title.</param>
    /// <param name="cancellationToken">Stops the generation.</param>
    /// <returns>The title in pieces, in order.</returns>
    IAsyncEnumerable<string> GenerateAsync(string callId, CancellationToken cancellationToken = default);
}
