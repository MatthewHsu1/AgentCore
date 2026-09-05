#pragma warning disable OPENAI001

using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Llm.OpenAI;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Llm;

/// <summary>
/// <c>providers.llm[].reasoningEffort</c>, and why a document ever writes it.
/// </summary>
/// <remarks>
/// <para>
/// The adapter sends this on the <c>/v1/responses</c> path, which is the only path where a
/// reasoning model accepts function tools at an effort above <c>none</c>. The value therefore
/// reaches the vendor inside <see cref="CreateResponseOptions"/>, not inside chat-completion
/// options, and these tests pin that shape.
/// </para>
/// <para>
/// Every test here runs offline. No request leaves the process and no key is real.
/// </para>
/// </remarks>
public sealed class OpenAiReasoningEffortTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public async Task EveryValueTheSchemaAllows_ReachesTheRequestTheVendorSees(string effort)
    {
        CapturingChatClient inner = new();

        var client = OpenAiChatClientAdapter.WithResponseDefaults(inner, effort);
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        var raw = Assert.IsType<CreateResponseOptions>(
            inner.Seen!.RawRepresentationFactory!(inner));

        Assert.Equal(effort, raw.ReasoningOptions!.ReasoningEffortLevel.ToString());
    }

    [Fact]
    public async Task TheLevelThatSendsNoReasoningAtAll_IsTheOneNamedNone()
    {
        // Pinned, so a vendor rename is caught here rather than as a 400 at run time against a real
        // provider. The SDK marks this type for evaluation (OPENAI001); this is what watches it.
        CapturingChatClient inner = new();

        var client = OpenAiChatClientAdapter.WithResponseDefaults(inner, "none");
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        var raw = Assert.IsType<CreateResponseOptions>(inner.Seen!.RawRepresentationFactory!(inner));

        Assert.Equal(ResponseReasoningEffortLevel.None, raw.ReasoningOptions!.ReasoningEffortLevel);
        Assert.Equal("none", ResponseReasoningEffortLevel.None.ToString());
    }

    [Fact]
    public async Task RawOptionsTheCallerBuiltItself_KeepTheValuesTheyAlreadyHold()
    {
        // A default for the entry, not an override of the call. The caller's own object is the one
        // the vendor sees, and every value it already carries survives.
        CapturingChatClient inner = new();
        CreateResponseOptions mine = new()
        {
            ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = ResponseReasoningEffortLevel.High,
            },
            StoredOutputEnabled = true,
        };

        var client = OpenAiChatClientAdapter.WithResponseDefaults(inner, "none");
        await client.GetResponseAsync(
            "hi",
            new ChatOptions { RawRepresentationFactory = _ => mine },
            TestContext.Current.CancellationToken);

        Assert.Same(mine, inner.Seen!.RawRepresentationFactory!(inner));
        Assert.Equal(ResponseReasoningEffortLevel.High, mine.ReasoningOptions!.ReasoningEffortLevel);
        Assert.True(mine.StoredOutputEnabled);
    }

    [Theory]
    [InlineData("high")]
    [InlineData(null)]
    public async Task EveryRequest_TellsTheVendorNotToStoreTheConversation(string? effort)
    {
        // The call store owns the transcript. A stored response comes back with a conversation id,
        // and ChatClientAgent refuses that next to the ChatHistoryProvider every agent carries.
        CapturingChatClient inner = new();

        var client = OpenAiChatClientAdapter.WithResponseDefaults(inner, effort);
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        var raw = Assert.IsType<CreateResponseOptions>(inner.Seen!.RawRepresentationFactory!(inner));

        Assert.False(raw.StoredOutputEnabled);
    }

    [Fact]
    public async Task AnEntryThatNamesNoEffort_SendsNoReasoningValueAtAll()
    {
        // Omitting it must not start sending a value the vendor would otherwise have chosen for
        // itself. Only the store flag rides along.
        CapturingChatClient inner = new();

        var client = OpenAiChatClientAdapter.WithResponseDefaults(inner, null);
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        var raw = Assert.IsType<CreateResponseOptions>(inner.Seen!.RawRepresentationFactory!(inner));

        Assert.Null(raw.ReasoningOptions);
    }

    [Fact]
    public void AValueThisVendorDoesNotKnow_FailsAndSaysWhatIsAllowed()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => OpenAiChatClientAdapter.WithResponseDefaults(new CapturingChatClient(), "exhaustive"));

        Assert.Contains(failure.Errors, error => error.Pointer == "/providers/llm");
        Assert.Contains("exhaustive", failure.Message, StringComparison.Ordinal);
        Assert.Contains("none", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryThatNamesNoEffort_StillCarriesTheStoreFlag()
    {
        MapSecretResolver secrets = new MapSecretResolver()
            .With(OpenAiChatClientAdapter.ApiKeySecretName, "sk-not-a-real-key");

        var client = await new OpenAiChatClientAdapter().CreateClientAsync(
            new LlmProviderConfiguration
            {
                Kind = OpenAiChatClientAdapter.ProviderKind,
                Model = "gpt-5.6-luna",
                As = "reply",
            },
            secrets,
            TestContext.Current.CancellationToken);

        // A wrapped client answers GetService for the configuring middleware; a bare one does not.
        Assert.NotNull(client.GetService(typeof(ConfigureOptionsChatClient)));
    }

    /// <summary>Records the options it was called with, and answers nothing.</summary>
    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? Seen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Seen = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
