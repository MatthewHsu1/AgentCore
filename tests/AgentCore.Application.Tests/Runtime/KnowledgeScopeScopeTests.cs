using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

public sealed class KnowledgeScopeScopeTests
{
    [Fact]
    public void Current_NoScopeOpen_IsNull() => Assert.Null(KnowledgeScopeScope.Current);

    [Fact]
    public void Current_InsideAnOpenScope_IsThatScope()
    {
        var scope = new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

        using (KnowledgeScopeScope.Open(scope))
        {
            Assert.Same(scope, KnowledgeScopeScope.Current);
        }

        Assert.Null(KnowledgeScopeScope.Current);
    }

    [Fact]
    public async Task Current_AcrossAnAwait_SurvivesInsideTheScope()
    {
        var scope = new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

        using (KnowledgeScopeScope.Open(scope))
        {
            await Task.Yield();
            Assert.Same(scope, KnowledgeScopeScope.Current);
        }
    }

    [Fact]
    public async Task Current_ConcurrentTurns_DoNotSeeEachOther()
    {
        using var barrier = new Barrier(8);

        var seen = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var mine = new KnowledgeScope { Facets = new Dictionary<string, string> { ["model"] = $"m{i}" } };
            using (KnowledgeScopeScope.Open(mine))
            {
                barrier.SignalAndWait();
                return KnowledgeScopeScope.Current!.Facets["model"];
            }
        })));

        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"m{i}"), seen);
    }
}
