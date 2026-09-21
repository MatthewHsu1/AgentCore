using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Domain.Tests.Knowledge
{
    public sealed class KnowledgeScopeTests
    {
        [Fact]
        public void TwoScopes_WithTheSameFacetsAndNoOrigins_AreEqual()
        {
            IReadOnlyDictionary<string, string> facets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["brand"] = "sole",
            };

            KnowledgeScope first = new() { Facets = facets };
            KnowledgeScope second = new() { Facets = facets };

            Assert.Equal(first, second);
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
        }
    }
}
