using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// The new providers.knowledge block. Every default must reproduce the value that was hardcoded
/// before it existed, so a document written against the old schema keeps its exact behaviour.
/// </summary>
public sealed class KnowledgeProviderSchemaTests
{
    private const string MinimalYaml =
        """
        apiVersion: agentcore/v1
        name: knowledge-defaults
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge: { kind: qdrant, endpoint: "https://q.example.com:6334", collection: kb }
        agents:
          items:
            - { id: only, instructions: "hello" }
        """;

    private const string FullYaml =
        """
        apiVersion: agentcore/v1
        name: knowledge-full
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            endpoint: "https://q.example.com:6334"
            collection: docs
            vector: embedding
            fields:
              id: doc_id
              body: content
              lexical: content
              source: origin
              locator: page
              authority: trust
            scope:
              template: "{key}"
            links:
              field: related
              lookup: filter
              namespace: dns
              prefix: "doc:"
            analyzer: none
            scoreFloor: 0.5
        agents:
          items:
            - { id: only, instructions: "hello" }
        """;

    private const string UnknownKeyYaml =
        """
        apiVersion: agentcore/v1
        name: knowledge-typo
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge: { kind: qdrant, endpoint: "https://q.example.com:6334", vectorName: dense }
        agents:
          items:
            - { id: only, instructions: "hello" }
        """;

    [Fact]
    public void Defaults_ReproduceTheOldHardcodedValues()
    {
        var knowledge = Load(MinimalYaml).Providers!.Knowledge!;

        Assert.Null(knowledge.Vector);
        Assert.Equal("card_id", knowledge.Fields.Id);
        Assert.Equal("body", knowledge.Fields.Body);
        Assert.Equal("text", knowledge.Fields.Lexical);
        Assert.Equal("source.ref", knowledge.Fields.Source);
        Assert.Equal("source.locator", knowledge.Fields.Locator);
        Assert.Equal("authority", knowledge.Fields.Authority);
        Assert.Equal("facets.{key}", knowledge.Scope.Template);
        Assert.Null(knowledge.Links);
        Assert.Equal("identifier-codes", knowledge.Analyzer);
        Assert.Equal(0.25, knowledge.ScoreFloor);
    }

    [Fact]
    public void EveryFieldBinds()
    {
        var knowledge = Load(FullYaml).Providers!.Knowledge!;

        Assert.Equal("embedding", knowledge.Vector);
        Assert.Equal("doc_id", knowledge.Fields.Id);
        Assert.Equal("content", knowledge.Fields.Body);
        Assert.Equal("content", knowledge.Fields.Lexical);
        Assert.Equal("origin", knowledge.Fields.Source);
        Assert.Equal("page", knowledge.Fields.Locator);
        Assert.Equal("trust", knowledge.Fields.Authority);
        Assert.Equal("{key}", knowledge.Scope.Template);
        Assert.NotNull(knowledge.Links);
        Assert.Equal("related", knowledge.Links!.Field);
        Assert.Equal(KnowledgeLinkLookup.Filter, knowledge.Links.Lookup);
        Assert.Equal("dns", knowledge.Links.Namespace);
        Assert.Equal("doc:", knowledge.Links.Prefix);
        Assert.Equal("none", knowledge.Analyzer);
        Assert.Equal(0.5, knowledge.ScoreFloor);
    }

    [Fact]
    public void UnknownKey_IsRejectedBySchemaValidation()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(() => Load(UnknownKeyYaml));

        Assert.Contains("vectorName", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeTemplate_WithoutKeyPlaceholder_IsRejectedBySchemaValidation()
    {
        const string yaml =
            """
            apiVersion: agentcore/v1
            name: knowledge-bad-template
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                endpoint: "https://q.example.com:6334"
                collection: kb
                scope:
                  template: "facets.constant"
            agents:
              items:
                - { id: only, instructions: "hello" }
            """;

        Assert.Throws<ConfigurationLoadException>(() => Load(yaml));
    }

    private static AgentCoreConfiguration Load(string yaml) => ConfigurationLoader.LoadYaml(yaml);
}
