using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// Compaction reaching a compiled agent's context providers, and the trimming reaching the model.
/// The block itself is D15's ledger: <see cref="CompactionStrategyFactoryTests"/> in
/// <c>Runtime</c> covers the stages and the unknown-model compile failure.
/// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
public sealed class CompactionCompilationTests
{
    [Fact]
    public void Compile_ADocumentThatStillWritesCompaction_FailsSchemaValidationLikeAnyUnknownKey()
    {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              defaults:
                compaction: { strategy: truncate }
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains("compaction", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compile_AnAgent_KeepsReaderContentOutOfTheModelRequest()
    {
        using SequencedChatClient reply = new("noted.");
        var compiled = CompileOneAgent(reply);
        var agent = Assert.Single(compiled.Agents.Values);
        var token = TestContext.Current.CancellationToken;
        var session = await agent.CreateSessionAsync(token);

        ChatMessage published = new(ChatRole.Tool, [new FunctionResultContent("call-1", "published")]);
        published.Contents.Add(new FileContent { Name = "report.md", FileId = "out/report.md", Kept = true });
        published.Contents.Add(new RenderContent
        {
            Name = "card",
            RenderId = "card-1",
            Data = JsonSerializer.SerializeToElement(new { title = "Report" }),
        });
        published.Contents.Add(new SourceContent
        {
            Source = new SourceReference { SourceId = "doc-1", Kind = SourceKind.Document, Title = "Doc", Origin = "knowledge" },
            CallId = "call-1",
        });
        List<ChatMessage> conversation =
        [
            new ChatMessage(ChatRole.User, "make me a report"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "file.publish")]),
            published,
            new ChatMessage(ChatRole.User, "thanks"),
        ];

        await agent.RunAsync(conversation, session, cancellationToken: token);

        Assert.DoesNotContain(
            reply.Requests[^1].SelectMany(message => message.Contents),
            content => content is FileContent or RenderContent or SourceContent);
    }

    private static CompiledAgent CompileOneAgent(SequencedChatClient reply) => ConfigurationCompiler.CompileAll(
        new AgentCoreConfiguration
        {
            ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
            Agents = new AgentsConfiguration
            {
                Items = [new AgentConfiguration { Id = "only" }],
            },
            Entries = new Dictionary<string, EntryConfiguration>
            {
                ["main"] = new EntryConfiguration { Agent = "only" },
            },
        },
        new AgentCompilationContext(new FakeChatClientFactory(reply)))["main"];
}
#pragma warning restore MAAI001
