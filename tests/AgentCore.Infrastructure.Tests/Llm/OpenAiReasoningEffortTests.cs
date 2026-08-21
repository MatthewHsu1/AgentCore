#pragma warning disable OPENAI001

using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Llm.OpenAI;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using ChatReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Llm;

/// <summary>
/// <c>providers.llm[].reasoningEffort</c>, and why a document ever writes it.
/// </summary>
/// <remarks>
/// <para>
/// A reasoning model refuses function tools on the chat-completions path unless the value is
/// <c>none</c>. Without this setting, the first agent that lists a tool fails every turn with a
/// vendor 400 naming <c>reasoning_effort</c> — and the log calls it a tool that failed four times,
/// which is not what happened.
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

        var client = OpenAiChatClientAdapter.WithReasoningEffort(inner, effort);
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        var raw = Assert.IsType<ChatCompletionOptions>(
            inner.Seen!.RawRepresentationFactory!(inner));

        Assert.Equal(effort, raw.ReasoningEffortLevel.ToString());
    }

    [Fact]
    public async Task TheValueThatLetsAReasoningModelUseFunctionTools_IsTheOneNamedNone()
    {
        // Pinned, so a vendor rename is caught here rather than as a 400 at run time against a real
        // provider. The SDK marks this type for evaluation (OPENAI001); this is what watches it.
        CapturingChatClient inner = new();

        var client = OpenAiChatClientAdapter.WithReasoningEffort(inner, "none");
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        var raw = Assert.IsType<ChatCompletionOptions>(inner.Seen!.RawRepresentationFactory!(inner));

        Assert.Equal(ChatReasoningEffortLevel.None, raw.ReasoningEffortLevel);
        Assert.Equal("none", ChatReasoningEffortLevel.None.ToString());
    }

    [Fact]
    public async Task RawOptionsTheCallerBuiltItself_AreLeftAlone()
    {
        // A default for the entry, not an override of the call.
        CapturingChatClient inner = new();
        ChatCompletionOptions mine = new();

        var client = OpenAiChatClientAdapter.WithReasoningEffort(inner, "none");
        await client.GetResponseAsync(
            "hi",
            new ChatOptions { RawRepresentationFactory = _ => mine },
            TestContext.Current.CancellationToken);

        Assert.Same(mine, inner.Seen!.RawRepresentationFactory!(inner));
    }

    [Fact]
    public void AValueThisVendorDoesNotKnow_FailsAndSaysWhatIsAllowed()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => OpenAiChatClientAdapter.WithReasoningEffort(new CapturingChatClient(), "exhaustive"));

        Assert.Contains(failure.Errors, error => error.Pointer == "/providers/llm");
        Assert.Contains("exhaustive", failure.Message, StringComparison.Ordinal);
        Assert.Contains("none", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryThatNamesNoEffort_IsNotWrappedAtAll()
    {
        // Omitting it must not start sending a value the vendor would otherwise have chosen for
        // itself. The adapter returns the bare client.
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
        Assert.Null(client.GetService(typeof(ConfigureOptionsChatClient)));
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
