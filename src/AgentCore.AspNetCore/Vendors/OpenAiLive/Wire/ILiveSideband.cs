namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Wire
{
    /// <summary>The text channel to one live GPT-Live call: one JSON event per message, both ways.</summary>
    internal interface ILiveSideband : IAsyncDisposable
    {
        /// <returns>The next whole message, or <see langword="null"/> once the socket closed.</returns>
        ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken);

        ValueTask SendAsync(string json, CancellationToken cancellationToken);
    }
}
