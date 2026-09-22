using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// An offline vendor adapter: one <c>kind</c>, one client factory, no socket and no key.
    /// </summary>
    internal sealed class FakeChatClientAdapter(string kind, Func<IChatClient> client) : IChatClientAdapter
    {
        private readonly Func<IChatClient> _client = client;

        public string Kind { get; } = kind;

        public ValueTask<IChatClient> CreateClientAsync(
            LlmProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_client());
        }
    }
}
