using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>
/// An offline vendor adapter: one <c>kind</c>, one client factory, no socket and no key.
/// </summary>
internal sealed class FakeChatClientAdapter : IChatClientAdapter
{
    private readonly Func<IChatClient> _client;

    public FakeChatClientAdapter(string kind, Func<IChatClient> client)
    {
        Kind = kind;
        _client = client;
    }

    public string Kind { get; }

    public ValueTask<IChatClient> CreateClientAsync(
        LlmProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_client());
}
