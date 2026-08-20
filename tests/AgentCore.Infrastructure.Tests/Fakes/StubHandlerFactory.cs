namespace AgentCore.Infrastructure.Tests.Fakes;

/// <summary>
/// An offline pipeline that hands out one handler, and records every name it is asked for.
/// </summary>
/// <remarks>
/// A host passes <c>AgentCoreHttpClients</c> here, which is where the connection lifetime, the
/// deadline, and the retry live. A test passes this, so an adapter reaches no network and the
/// recorded names prove which client an adapter opens.
/// </remarks>
internal sealed class StubHandlerFactory : IHttpMessageHandlerFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHandlerFactory(HttpMessageHandler handler) => _handler = handler;

    /// <summary>Gets every client name this factory was asked for, in call order.</summary>
    public List<string> Names { get; } = [];

    public HttpMessageHandler CreateHandler(string name)
    {
        Names.Add(name);
        return _handler;
    }
}
