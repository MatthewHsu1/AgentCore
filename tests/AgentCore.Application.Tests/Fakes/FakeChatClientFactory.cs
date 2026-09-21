using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>
    /// Hands the same offline client to every model reference. No network, and no API key.
    /// </summary>
    internal sealed class FakeChatClientFactory(IChatClient client, int contextWindow = 128_000) : IChatClientFactory
    {
        private readonly IChatClient _client = client;
        private readonly int _contextWindow = contextWindow;

        /// <summary>Gets every model reference this factory was asked for, in call order.</summary>
        public List<ModelReference?> Requested { get; } = [];

        public IChatClient GetChatClient(ModelReference? model)
        {
            lock (Requested)
            {
                Requested.Add(model);
            }

            return _client;
        }

        public int? GetContextWindow(ModelReference? model)
        {
            return _contextWindow;
        }
    }
}
