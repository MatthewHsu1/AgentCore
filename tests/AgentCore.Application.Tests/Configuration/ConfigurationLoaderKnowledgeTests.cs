using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration
{
    /// <summary>
    /// The knowledge provider of a document binds: its store, its fields, its mapper and its links.
    /// </summary>
    public sealed class ConfigurationLoaderKnowledgeTests
    {
        [Fact]
        public void AnEmptyKnowledgeBlock_FailsTheLoadForKindAndCollection()
        {
            // There is nothing to default them to. AgentCore knows no vendor and no collection name, so
            // an empty block names no store at all rather than naming a conventional one.
            const string document = """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge: {}
            """;

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

            Assert.Contains("kind", failure.Message, StringComparison.Ordinal);
            Assert.Contains("collection", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AFieldsBlockNamingOneRole_LeavesEveryOtherRoleUnmapped()
        {
            // The whole point of the block: a role this document does not name is absent from the card.
            // It is never filled in from a name AgentCore chose, because AgentCore chooses none.
            const string document = """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                collection: pages
                fields: { body: page_content }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            KnowledgeProviderConfiguration knowledge = ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!;

            Assert.Equal("page_content", knowledge.Fields!.Body);
            Assert.Null(knowledge.Fields.Id);
            Assert.Null(knowledge.Fields.Lexical);
            Assert.Null(knowledge.Fields.Source);
            Assert.Null(knowledge.Fields.Locator);
            Assert.Null(knowledge.Fields.Authority);
            Assert.Null(knowledge.Scope.Template);
        }

        [Fact]
        public void ABodyFieldSetToNull_FailsTheLoad()
        {
            const string document = """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                collection: pages
                fields: { body: null }
            """;

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

            Assert.Contains(
                failure.Errors,
                error => error.Pointer!.StartsWith("/providers/knowledge/fields", StringComparison.Ordinal));
        }

        [Fact]
        public void AKnowledgeProviderThatWritesEveryField_BindsThem()
        {
            const string document = """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                endpoint: https://cluster.example.com:6334
                collection: manuals
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            KnowledgeProviderConfiguration knowledge = configuration.Providers!.Knowledge!;
            Assert.Equal("qdrant", knowledge.Kind);
            Assert.Equal("https://cluster.example.com:6334", knowledge.Endpoint);
            Assert.Equal("manuals", knowledge.Collection);
        }

        [Fact]
        public void AKnowledgeProviderWithNoVector_BindsNullMeaningTheAnonymousVector()
        {
            const string document = """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge: { kind: qdrant, collection: manuals }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            KnowledgeProviderConfiguration knowledge = ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!;

            Assert.Null(knowledge.Vector);
        }

        [Fact]
        public void AKnowledgeProviderNamingAMapper_BindsIt()
        {
            const string document = """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                collection: manuals
                mapper: acme-catalog
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            Assert.Equal("acme-catalog", ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!.Mapper);
        }

        [Fact]
        public void NoLinksBlock_LeavesLinksNullAndTheFeatureOff()
        {
            const string document = """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge: { kind: qdrant, collection: manuals }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            Assert.Null(ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!.Links);
        }

        [Fact]
        public void ALinksBlockWithoutLookup_DefaultsToFilter()
        {
            // filter is the only mode that works on any collection. uuid5 and direct derive the key.
            const string document = """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                collection: manuals
                links: { field: related }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            KnowledgeLinksConfiguration? links = ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!.Links;

            Assert.NotNull(links);
            Assert.Equal(KnowledgeLinkLookup.Filter, links!.Lookup);
            Assert.Equal("related", links.Field);
        }
    }
}
