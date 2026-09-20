#pragma warning disable OPENAI001

using AgentCore.Application.Llm;
using AgentCore.Infrastructure.Llm.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Llm;

/// <summary>
/// The conversation id the runtime stamps under <see cref="ChatRequestProperties.ConversationId"/>
/// reaches the vendor as <c>prompt_cache_key</c>, so every turn of one conversation lands on the
/// same prompt cache. Every test here runs offline.
/// </summary>
public sealed class OpenAiPromptCacheKeyTests
{
    [Fact]
    public async Task AStampedConversationId_BecomesThePromptCacheKey()
    {
        CapturingChatClient inner = new();

        var client = OpenAiChatClientAdapter.WithResponseDefaults(inner, effort: null);
        await client.GetResponseAsync(
            "hi",
            new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [ChatRequestProperties.ConversationId] = "conv-42",
                },
            },
            TestContext.Current.CancellationToken);

        var raw = Assert.IsType<CreateResponseOptions>(inner.Seen!.RawRepresentationFactory!(inner));

        Assert.Equal("conv-42", raw.PromptCacheKey);
    }

    [Fact]
    public async Task ARequestOutsideAConversation_SendsNoPromptCacheKey()
    {
        CapturingChatClient inner = new();

        var client = OpenAiChatClientAdapter.WithResponseDefaults(inner, effort: null);
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        var raw = Assert.IsType<CreateResponseOptions>(inner.Seen!.RawRepresentationFactory!(inner));

        Assert.Null(raw.PromptCacheKey);
    }

    [Fact]
    public async Task AKeyTheCallerSetItself_Wins()
    {
        CapturingChatClient inner = new();
        CreateResponseOptions mine = new() { PromptCacheKey = "caller-key" };

        var client = OpenAiChatClientAdapter.WithResponseDefaults(inner, effort: null);
        await client.GetResponseAsync(
            "hi",
            new ChatOptions
            {
                RawRepresentationFactory = _ => mine,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [ChatRequestProperties.ConversationId] = "conv-42",
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("caller-key", mine.PromptCacheKey);
    }
}
