using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// A factory whose two seams are lambdas, for a test that needs one odd answer — a factory that
/// throws, or one that admits a hosted tool for one model only — without a class of its own.
/// </summary>
public sealed class LambdaChatClientFactory : IChatClientFactory
{
    private readonly Func<ModelReference?, IChatClient> _client;
    private readonly Func<AITool, ModelReference?, AITool?> _hostedTool;

    /// <param name="client">Answers <see cref="GetChatClient"/>. Omitted, every call throws.</param>
    /// <param name="hostedTool">Answers <see cref="ResolveHostedTool"/>. Omitted, no model admits a hosted tool.</param>
    public LambdaChatClientFactory(
        Func<ModelReference?, IChatClient>? client = null,
        Func<AITool, ModelReference?, AITool?>? hostedTool = null)
    {
        _client = client ?? (_ => throw new NotSupportedException());
        _hostedTool = hostedTool ?? ((_, _) => null);
    }

    public IChatClient GetChatClient(ModelReference? model) => _client(model);

    public AITool? ResolveHostedTool(AITool marker, ModelReference? model) => _hostedTool(marker, model);

    public int? GetContextWindow(ModelReference? model) => 128_000;
}
