using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.State;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge
{
    public sealed class StateKnowledgeScopeTests
    {
        private static readonly KnowledgeScopeConfiguration Configured = new()
        {
            Template = "facets.{key}",
            Wildcard = new() { Value = "*", Facets = ["brand", "applies_to"] },
            FromState = ["brand", "applies_to"],
        };

        private static StateDocument Document()
        {
            static StateSlotConfiguration Slot(params string[] members)
            {
                return new()
                {
                    Type = StateSlotType.String,
                    Writer = StateWriter.Extractor,
                    EnumValues = [.. members.Select(m => (JsonNode)JsonValue.Create(m)!)],
                };
            }

            return new StateDocument(new AgentCoreConfiguration
            {
                ApiVersion = "agentcore/v1",
                Agents = new AgentsConfiguration { Items = [] },
                Entries = new Dictionary<string, EntryConfiguration>(),
                State = new Dictionary<string, StateSlotConfiguration>(StringComparer.Ordinal)
                {
                    ["brand"] = Slot("sole", "spirit"),
                    ["applies_to"] = Slot("f63", "f80"),
                },
            });
        }

        [Fact]
        public void Compose_NothingKnown_IsAllWildcard()
        {
            KnowledgeScope? scope = StateKnowledgeScope.Compose(Document(), Configured, hostScope: null);

            Assert.Equal("*", scope!.Facets["brand"]);
            Assert.Equal("*", scope.Facets["applies_to"]);
        }

        [Fact]
        public void Compose_BrandKnown_LeavesTheMachineWildcard()
        {
            StateDocument state = Document();
            _ = state.TryWrite("brand", JsonValue.Create("sole"));

            KnowledgeScope? scope = StateKnowledgeScope.Compose(state, Configured, hostScope: null);

            Assert.Equal("sole", scope!.Facets["brand"]);
            Assert.Equal("*", scope.Facets["applies_to"]);
        }

        [Fact]
        public void Compose_HostAlreadySetTheKey_DoesNotOverwriteIt()
        {
            StateDocument state = Document();
            _ = state.TryWrite("brand", JsonValue.Create("sole"));
            KnowledgeScope host = new()
            {
                Facets = new Dictionary<string, string>(StringComparer.Ordinal) { ["brand"] = "spirit" },
            };

            KnowledgeScope? scope = StateKnowledgeScope.Compose(state, Configured, host);

            Assert.Equal("spirit", scope!.Facets["brand"]);
        }

        [Fact]
        public void Compose_NoFromState_ReturnsTheHostInstance()
        {
            KnowledgeScope host = new()
            {
                Facets = new Dictionary<string, string>(StringComparer.Ordinal) { ["brand"] = "sole" },
            };

            KnowledgeScope? scope = StateKnowledgeScope.Compose(
                Document(), Configured with { FromState = [] }, host);

            Assert.Same(host, scope);
        }

        [Fact]
        public void Compose_NoWildcard_ReturnsNull()
        {
            KnowledgeScope? scope = StateKnowledgeScope.Compose(
                Document(), Configured with { Wildcard = null }, hostScope: null);

            Assert.Null(scope);
        }

        [Fact]
        public void Compose_MarksEachFacetWithItsOrigin()
        {
            StateDocument state = Document();
            _ = state.TryWrite("brand", JsonValue.Create("sole"));
            KnowledgeScope ambient = new()
            {
                Facets = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "uk" },
            };

            KnowledgeScope scope = StateKnowledgeScope.Compose(state, Configured, ambient)!;

            Assert.Equal(KnowledgeFacetOrigin.Host, scope.Origins["region"]);
            Assert.Equal(KnowledgeFacetOrigin.Extractor, scope.Origins["brand"]);
            Assert.Equal(KnowledgeFacetOrigin.Wildcard, scope.Origins["applies_to"]);
        }
    }
}
