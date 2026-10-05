using System.Text.Json;

using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Xunit;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Knowledge
{
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
            AIFunction tool = await SearchToolAsync(new StubKnowledgePort([]), scope: null);

            Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("filters", out _));
        }

        [Fact]
        public async Task ToolMode_FilterableDeclared_PutsEveryKeyInTheEnum()
        {
            AIFunction tool = await SearchToolAsync(new StubKnowledgePort([]), Declared);

            List<string?> keys = [.. tool.JsonSchema
                .GetProperty("properties").GetProperty("filters")
                .GetProperty("items").GetProperty("properties").GetProperty("key")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString())];

            Assert.Equal(["model", "section"], keys);
        }

        [Fact]
        public async Task ToolMode_FilterableDeclared_TellsTheModelWhatEachKeyMeans()
        {
            AIFunction tool = await SearchToolAsync(new StubKnowledgePort([]), Declared);

            string? wording = tool.JsonSchema
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

            AIFunction tool = await SearchToolAsync(new StubKnowledgePort([]), scope);

            string? wording = tool.JsonSchema
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
            AIFunction tool = await SearchToolAsync(new StubKnowledgePort([]), Declared);

            string? wording = tool.JsonSchema
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
            AIFunction tool = await SearchToolAsync(port, Declared);

            _ = await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

            Assert.Equal("lcr-2023", port.ScopeAtTheStore!.Facets["model"]);
            Assert.Equal(KnowledgeFacetOrigin.Tool, port.ScopeAtTheStore.Origins["model"]);
        }

        [Fact]
        public async Task Filters_AreNotForwardedToTheInnerFunction()
        {
            StubKnowledgePort port = new([Card("a")]);
            AIFunction tool = await SearchToolAsync(port, Declared);

            _ = await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

            Assert.Equal("what belt fits it?", port.LastQuery);
        }

        [Fact]
        public async Task Filters_AKeyNobodyDeclared_IsIgnored()
        {
            StubKnowledgePort port = new([Card("a")]);
            AIFunction tool = await SearchToolAsync(port, Declared);

            _ = await CallAsync(tool, "anything", ("customer", "globex"));

            Assert.Empty(port.ScopeAtTheStore!.Facets);
        }

        [Fact]
        public async Task Filters_ClearNoCard_TheSearchRunsAgainWithoutThem()
        {
            // The whole point of the safety net: a filter is the model's guess at the subject, and a
            // guess that matches nothing must not become "nothing is documented".
            ScopeFilteringKnowledgePort port = new(
                (Card("belt"), new Dictionary<string, string>(StringComparer.Ordinal) { ["model"] = "lcr-2019" }));

            AIFunction tool = await SearchToolAsync(port, Declared);

            string answer = await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

            Assert.Contains("card belt", answer, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Filters_ClearNoCardAndNothingElseDoesEither_StaysEmpty()
        {
            ScopeFilteringKnowledgePort port = new();

            AIFunction tool = await SearchToolAsync(port, Declared);

            string answer = await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

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

            AIFunction tool = await SearchToolAsync(new StubKnowledgePort([Card("a")]), Declared, loggers: loggers);
            _ = await CallAsync(tool, "what belt fits it?", ("model", "lcr-2023"));

            CapturedLine line = Assert.Single(loggers.Of(11));
            KnowledgeAuditRecord.LogView? record = line.Field<KnowledgeAuditRecord.LogView>("Record");

            Assert.NotNull(record);
            Assert.Equal("model=lcr-2023 (Tool)", record!.Scope);
        }

        [Fact]
        public async Task Invoking_ToolFromEarlierProvider_LeavesItUnwrapped()
        {
            // Arrange
            AIFunction skillTool = AIFunctionFactory.Create(
                (string skillName) => "skill-body:" + skillName,
                "load_skill",
                "Loads the full content of a skill.");
            AIContextProvider provider = KnowledgeProviderFactory.Create(
                new StubKnowledgePort([Card("a")]),
                new ResolvedKnowledge(KnowledgeMode.Tool, Limit: 5, Citations: false, Scoped: false),
                "agent-under-test",
                new SourceLocatorCitationFormatter(),
                null,
                Declared);

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            AIContextProvider.InvokingContext context = new(
                StubAgent.Instance,
                new StubSession(),
                new AIContext
                {
                    Messages = [new ChatMessage(ChatRole.User, "hello")],
                    Tools = [skillTool],
                });
#pragma warning restore MAAI001

            // Act
            AIContext provided = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);

            // Assert
            List<AITool> tools = Assert.IsType<List<AITool>>(provided.Tools);
            Assert.Same(skillTool, tools[0]);
            _ = Assert.IsType<FacetFilteredSearch>(tools[1], exactMatch: false);
        }

        private static async Task<AIFunction> SearchToolAsync(
            IKnowledgeRetrievalPort port,
            KnowledgeScopeConfiguration? scope,
            ILoggerFactory? loggers = null)
        {
            AIContextProvider provider = KnowledgeProviderFactory.Create(
                port,
                new ResolvedKnowledge(KnowledgeMode.Tool, Limit: 5, Citations: false, Scoped: false),
                "agent-under-test",
                new SourceLocatorCitationFormatter(),
                loggers,
                scope);

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            AIContext context = await provider.InvokingAsync(
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
            };

            TurnInvocation turn = new()
            {
                ConversationId = "conversation",
                TurnIndex = 0,
                Stage = "",
                Knowledge = new KnowledgeScope { Facets = new Dictionary<string, string>(StringComparer.Ordinal) },
            };


            return await tool.InvokeAsync(
                turn.FileIn(new AIFunctionArguments(arguments)), TestContext.Current.CancellationToken) is not IReadOnlyList<TextSearchProvider.TextSearchResult> results ? string.Empty : string.Join("\n", results.Select(result => result.Text));
        }

        private sealed class StubSession : AgentSession;

        /// <summary>Stands in for the agent the framework names on a context. Nothing here runs it.</summary>
        private sealed class StubAgent : AIAgent
        {
            public static StubAgent Instance { get; } = new();

            protected override ValueTask<AgentSession> CreateSessionCoreAsync(
                CancellationToken cancellationToken = default)
            {
                return new(new StubSession());
            }

            protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
                AgentSession session,
                JsonSerializerOptions? jsonSerializerOptions = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
                JsonElement serializedState,
                JsonSerializerOptions? jsonSerializerOptions = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override Task<AgentResponse> RunCoreAsync(
                IEnumerable<ChatMessage> messages,
                AgentSession? session = null,
                AgentRunOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
                IEnumerable<ChatMessage> messages,
                AgentSession? session = null,
                AgentRunOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private static KnowledgeCard Card(string id)
        {
            return new()
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
    }
}
