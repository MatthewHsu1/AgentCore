using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge
{
    /// <summary>
    /// What a tool argument is allowed to change about the scope a turn already composed.
    /// </summary>
    /// <remarks>
    /// The rule these hold is an isolation rule, not a retrieval one. A host facet is who is asking and
    /// an extractor facet is what they said; a facet off a tool argument is a guess about the subject. A
    /// guess that could overwrite either would be an argument that moves the caller into another tenant.
    /// </remarks>
    public sealed class ToolFacetOverlayTests
    {
        [Fact]
        public void Apply_NamedNothing_LeavesTheScopeAlone()
        {
            KnowledgeScope composed = Scope((KnowledgeFacetOrigin.Host, "customer", "acme"));

            (KnowledgeScope? scope, bool narrowed) = ToolFacetOverlay.Apply(composed, named: null);

            Assert.Same(composed, scope);
            Assert.False(narrowed);
        }

        [Fact]
        public void Apply_HostFacet_IsNotOverwritten()
        {
            KnowledgeScope composed = Scope((KnowledgeFacetOrigin.Host, "customer", "acme"));

            (KnowledgeScope? scope, bool narrowed) = ToolFacetOverlay.Apply(composed, Named(("customer", "globex")));

            Assert.Equal("acme", scope.Facets["customer"]);
            Assert.Equal(KnowledgeFacetOrigin.Host, scope.Origins["customer"]);
            Assert.False(narrowed);
        }

        [Fact]
        public void Apply_ExtractorFacet_IsNotOverwritten()
        {
            KnowledgeScope composed = Scope((KnowledgeFacetOrigin.Extractor, "product_line", "treadmill"));

            (KnowledgeScope? scope, bool narrowed) = ToolFacetOverlay.Apply(composed, Named(("product_line", "bike")));

            Assert.Equal("treadmill", scope.Facets["product_line"]);
            Assert.False(narrowed);
        }

        [Fact]
        public void Apply_WildcardFacet_IsNarrowed()
        {
            KnowledgeScope composed = Scope((KnowledgeFacetOrigin.Wildcard, "model", "*"));

            (KnowledgeScope? scope, bool narrowed) = ToolFacetOverlay.Apply(composed, Named(("model", "lcr-2023")));

            Assert.Equal("lcr-2023", scope.Facets["model"]);
            Assert.Equal(KnowledgeFacetOrigin.Tool, scope.Origins["model"]);
            Assert.True(narrowed);
        }

        [Fact]
        public void Apply_FacetTheScopeDoesNotHold_IsAdded()
        {
            (KnowledgeScope? scope, bool narrowed) = ToolFacetOverlay.Apply(Scope(), Named(("model", "lcr-2023")));

            Assert.Equal("lcr-2023", scope.Facets["model"]);
            Assert.Equal(KnowledgeFacetOrigin.Tool, scope.Origins["model"]);
            Assert.True(narrowed);
        }

        [Fact]
        public void Apply_OneNarrowsAndOneIsRefused_KeepsBoth()
        {
            KnowledgeScope composed = Scope(
                (KnowledgeFacetOrigin.Host, "customer", "acme"),
                (KnowledgeFacetOrigin.Wildcard, "model", "*"));

            (KnowledgeScope? scope, bool narrowed) = ToolFacetOverlay.Apply(
                composed, Named(("customer", "globex"), ("model", "lcr-2023")));

            Assert.Equal("acme", scope.Facets["customer"]);
            Assert.Equal("lcr-2023", scope.Facets["model"]);
            Assert.True(narrowed);
        }

        [Fact]
        public void Apply_DoesNotMutateTheScopeItWasGiven()
        {
            KnowledgeScope composed = Scope((KnowledgeFacetOrigin.Wildcard, "model", "*"));

            _ = ToolFacetOverlay.Apply(composed, Named(("model", "lcr-2023")));

            Assert.Equal("*", composed.Facets["model"]);
        }

        private static KnowledgeScope Scope(params (KnowledgeFacetOrigin Origin, string Key, string Value)[] facets)
        {
            return new()
            {
                Facets = facets.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal),
                Origins = facets.ToDictionary(f => f.Key, f => f.Origin, StringComparer.Ordinal),
            };
        }

        private static Dictionary<string, string> Named(params (string Key, string Value)[] facets)
        {
            return facets.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        }
    }
}
