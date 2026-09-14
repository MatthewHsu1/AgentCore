using System.Text.Json;

using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The <c>filters</c> argument a tool-mode agent narrows its own search with.
/// </summary>
public sealed class FacetFilterTests
{
    private static readonly KnowledgeScopeConfiguration Declared = new()
    {
        Template = "facets.{key}",
        Filterable =
        [
            new() { Key = "model", Description = "The machine and the year, as one tag." },
            new() { Key = "section", Description = "Which part of the manual." },
        ],
    };

    [Fact]
    public async Task ToolMode_NothingFilterable_OffersThePlainSearch()
    {
        var tool = await SearchToolAsync(new StubKnowledgePort([]), scope: null);

        Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("filters", out _));
    }

    [Fact]
    public async Task ToolMode_FilterableDeclared_PutsEveryKeyInTheEnum()
    {
        var tool = await SearchToolAsync(new StubKnowledgePort([]), Declared);

        var keys = tool.JsonSchema
            .GetProperty("properties").GetProperty("filters")
            .GetProperty("items").GetProperty("properties").GetProperty("key")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        Assert.Equal(["model", "section"], keys);
    }

    [Fact]
    public async Task ToolMode_FilterableDeclared_TellsTheModelWhatEachKeyMeans()
    {
        var tool = await SearchToolAsync(new StubKnowledgePort([]), Declared);

        var wording = tool.JsonSchema
            .GetProperty("properties").GetProperty("filters")
            .GetProperty("description")
            .GetString();

        Assert.Contains("model: The machine and the year, as one tag.", wording, StringComparison.Ordinal);
        Assert.Contains("section: Which part of the manual.", wording, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolMode_FacetWithResolve_TellsTheModelHowToFindTheValue()
    {
        KnowledgeScopeConfiguration scope = new()
        {
            Template = "facets.{key}",
            Filterable =
            [
                new() { Key = "lookup", Description = "The only value is model-numbers." },
                new()
                {
                    Key = "model",
                    Description = "The machine and the year, as one tag.",
                    Resolve = new()
                    {
                        Via = new() { Key = "lookup", Value = "model-numbers" },
                        Query = "<product> model number",
                        Read = "the Tag column of the row for the person's year",
                    },
                },
            ],
        };

        var tool = await SearchToolAsync(new StubKnowledgePort([]), scope);

        var wording = tool.JsonSchema
            .GetProperty("properties").GetProperty("filters")
            .GetProperty("description")
            .GetString();

        Assert.Contains(
            "model: The machine and the year, as one tag. To find the value: search once for "
            + "\"<product> model number\" with filters [{key: lookup, value: model-numbers}], and read "
            + "the Tag column of the row for the person's year. Copy it exactly; never build one.",
            wording,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolMode_FilterableDeclared_SaysTheValueIsMatchedExactly()
    {
        var tool = await SearchToolAsync(new StubKnowledgePort([]), Declared);

        var wording = tool.JsonSchema
            .GetProperty("properties").GetProperty("filters")
            .GetProperty("items").GetProperty("properties").GetProperty("value")
            .GetProperty("description")
            .GetString();

        Assert.Equal(
            "The value, exactly as the cards store it; it is matched exactly. A key that says how to "
            + "find its value must be found that way first.",
            wording);
    }

    [Fact]
    public async Task Filters_NarrowTheScopeTheStoreSees()
    {
        StubKnowledgePort port = new([Card("a")]);
        var tool = await SearchToolAsync(port, Declared);

        await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

        Assert.Equal("lcr-2023", port.ScopeAtTheStore!.Facets["model"]);
        Assert.Equal(KnowledgeFacetOrigin.Tool, port.ScopeAtTheStore.Origins["model"]);
    }

    [Fact]
    public async Task Filters_AreNotForwardedToTheInnerFunction()
    {
        StubKnowledgePort port = new([Card("a")]);
        var tool = await SearchToolAsync(port, Declared);

        await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

        Assert.Equal("what belt fits it?", port.LastQuery);
    }

    [Fact]
    public async Task Filters_AKeyNobodyDeclared_IsIgnored()
    {
        StubKnowledgePort port = new([Card("a")]);
        var tool = await SearchToolAsync(port, Declared);

        await CallAsync(tool, "anything", ("customer", "globex"));

        Assert.Empty(port.ScopeAtTheStore!.Facets);
    }

    [Fact]
    public async Task Filters_ClearNoCard_TheSearchRunsAgainWithoutThem()
    {
        // The whole point of the safety net: a filter is the model's guess at the subject, and a
        // guess that matches nothing must not become "nothing is documented".
        ScopeFilteringKnowledgePort port = new(
            (Card("belt"), new Dictionary<string, string>(StringComparer.Ordinal) { ["model"] = "lcr-2019" }));

        var tool = await SearchToolAsync(port, Declared);

        var answer = await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

        Assert.Contains("card belt", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filters_ClearNoCardAndNothingElseDoesEither_StaysEmpty()
    {
        ScopeFilteringKnowledgePort port = new();

        var tool = await SearchToolAsync(port, Declared);

        var answer = await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

        Assert.DoesNotContain("card ", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filters_AreNamedInTheRecordAnOperatorDebugsFrom()
    {
        // The record is written while the search's own scope is still the composed one. Built a
        // line later, it names whatever the turn composed instead -- and the one thing an operator
        // opens this record to see, which facet narrowed the search that found nothing, is exactly
        // the part that goes missing.
        RecordingLoggerFactory loggers = new();

        var tool = await SearchToolAsync(new StubKnowledgePort([Card("a")]), Declared, loggers: loggers);
        await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

        var line = Assert.Single(loggers.Of(11));
        var record = line.Field<KnowledgeAuditRecord.LogView>("Record");

        Assert.NotNull(record);
        Assert.Equal("model=lcr-2023 (Tool)", record!.Scope);
    }

    private static async Task<AIFunction> SearchToolAsync(
        IKnowledgeRetrievalPort port,
        KnowledgeScopeConfiguration? scope,
        ILoggerFactory? loggers = null)
    {
        var provider = KnowledgeProviderFactory.Create(
            port,
            new ResolvedKnowledge(KnowledgeMode.Tool, Limit: 5, Citations: false, Scoped: false),
            "agent-under-test",
            new SourceLocatorCitationFormatter(),
            loggers,
            scope);

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        var context = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                StubAgent.Instance,
                new StubSession(),
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "hello")] }),
            TestContext.Current.CancellationToken);
#pragma warning restore MAAI001

        return Assert.IsType<AIFunction>(Assert.Single(context.Tools!), exactMatch: false);
    }

    private static async Task<string> CallAsync(
        AIFunction tool, string question, params (string Key, string Value)[] filters)
    {
        Dictionary<string, object?> arguments = new(StringComparer.Ordinal)
        {
            ["userQuestion"] = question,
            ["filters"] = JsonSerializer.SerializeToElement(
                filters.Select(filter => new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["key"] = filter.Key,
                    ["value"] = filter.Value,
                })),
            [TurnInvocation.ArgumentsKey] = new TurnInvocation
            {
                CallId = "call",
                TurnIndex = 0,
                Stage = "",
                Knowledge = new KnowledgeScope { Facets = new Dictionary<string, string>(StringComparer.Ordinal) },
            },
        };

        var results = await tool.InvokeAsync(
            new AIFunctionArguments(arguments), TestContext.Current.CancellationToken)
            as IReadOnlyList<TextSearchProvider.TextSearchResult>;

        return results is null ? string.Empty : string.Join("\n", results.Select(result => result.Text));
    }

    private sealed class StubSession : AgentSession;

    /// <summary>Stands in for the agent the framework names on a context. Nothing here runs it.</summary>
    private sealed class StubAgent : AIAgent
    {
        public static StubAgent Instance { get; } = new();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default)
            => new(new StubSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static KnowledgeCard Card(string id)
        => new()
        {
            CardId = id,
            Text = "card " + id,
            Authority = 3,
            SourceRef = "ct900-om",
            SourceLocator = "p.27",
            Score = 0.87,
            ViaLink = false,
        };
}
