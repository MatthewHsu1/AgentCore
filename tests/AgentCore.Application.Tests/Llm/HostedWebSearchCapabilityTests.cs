using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Llm;

/// <summary>
/// Which vendors resolve a hosted marker, and the operator's per-entry veto.
/// </summary>
public sealed class HostedWebSearchCapabilityTests
{
    [Fact]
    public void Adapter_DefaultMember_AnswersNull()
    {
        // A host that wrote an adapter before this member existed keeps compiling, and keeps
        // answering null, so its models never meet a tool they cannot call.
        IChatClientAdapter adapter = new SilentAdapter();

        Assert.Null(adapter.ResolveHostedTool(new HostedWebSearchTool(), Entry(webSearch: null)));
    }

    [Fact]
    public void Factory_UnknownReference_AnswersNull()
    {
        // A reference the document does not declare must not throw here. The compiler asks this
        // question while deciding whether to add a tool, and a missing model is reported elsewhere.
        IChatClientFactory factory = new SilentFactory();

        Assert.Null(factory.ResolveHostedTool(new HostedWebSearchTool(), new ModelReference { Ref = "absent" }));
    }

    private static LlmProviderConfiguration Entry(bool? webSearch) => new()
    {
        Kind = "silent",
        Model = "m",
        As = "reply",
        WebSearch = webSearch,
    };

    private sealed class SilentAdapter : IChatClientAdapter
    {
        public string Kind => "silent";

        public ValueTask<IChatClient> CreateClientAsync(
            LlmProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class SilentFactory : IChatClientFactory
    {
        public IChatClient GetChatClient(ModelReference? model)
            => throw new NotSupportedException();
    }
}
