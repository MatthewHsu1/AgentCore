using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// The providers.knowledge block. It ships no payload field names at all, so the block a deployment
/// writes is the only description of its collection that exists.
/// </summary>
/// <remarks>
/// This file used to assert the opposite: that every unwritten field took the name one particular
/// ingester happened to use. That made one corpus's naming the framework's, and made a mismapped
/// role silent instead of loud. The assertions below are the inversion of that.
/// </remarks>
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
            citation: acme-handbook
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
          knowledge: { kind: qdrant, endpoint: "https://q.example.com:6334", collection: kb, vectorName: dense }
        agents:
          items:
            - { id: only, instructions: "hello" }
        """;

    [Fact]
    public void ABlockThatNamesNoFields_MapsNothing()
    {
        var knowledge = Load(MinimalYaml).Providers!.Knowledge!;

        Assert.Null(knowledge.Vector);

        // Not an empty fields block with six null roles: no block at all. The document said nothing
        // about this collection's payload, and nothing is what AgentCore knows about it.
        Assert.Null(knowledge.Fields);
        Assert.Null(knowledge.Scope.Template);
        Assert.Null(knowledge.Links);

        // 'none' requires no term, so ranking is vector similarity alone. The old default named one
        // corpus's error-code shape and applied it to every consumer's queries.
        Assert.Equal("none", knowledge.Analyzer);

        // A wording IS shipped, unlike a field name: a citation has to read something, and the two
        // roles it reads are the two the document already named. The wording is replaceable, and
        // that is a different thing from being absent.
        Assert.Equal("source-locator", knowledge.Citation);
        Assert.Equal(0.25, knowledge.ScoreFloor);
    }

    [Fact]
    public void EveryFieldBinds()
    {
        var knowledge = Load(FullYaml).Providers!.Knowledge!;

        Assert.Equal("embedding", knowledge.Vector);
        Assert.Equal("doc_id", knowledge.Fields!.Id);
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
        Assert.Equal("acme-handbook", knowledge.Citation);
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
