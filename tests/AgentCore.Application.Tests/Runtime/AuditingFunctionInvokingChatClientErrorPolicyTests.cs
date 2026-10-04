using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.ToolCalls;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The tool error policy lives in <see cref="AuditingFunctionInvokingChatClient.InvokeFunctionAsync"/>, the
    /// framework's single choke point for every tool call. These tests pin its caller-observable behaviour, including
    /// that a plain <c>AIFunctionFactory.Create(...)</c> tool, which is not a <see cref="DeclaredTool"/> at all,
    /// gets identical treatment.
    /// </summary>
    public sealed class AuditingFunctionInvokingChatClientErrorPolicyTests
    {
        private static readonly ToolConfiguration LookupOrder = new()
        {
            Id = "lookup_order",
            Kind = ToolKind.Binding,
            Binds = "LookupOrder",
            Description = "Read one order by its identifier.",
        };

        // ---------------------------------------------------------------------------------------
        // 3. A fault the model CAN answer still becomes a ToolErrorResult the model reads.
        // ---------------------------------------------------------------------------------------
        [Fact]
        public async Task AFaultTheModelCanAnswer_BecomesTheErrorResultTheModelReads()
        {
            ThrowingDeclaredTool tool = new(LookupOrder, new InvalidOperationException("the order is already closed."));

            JsonObject result = await RunSingleRoundAsync(tool, TestContext.Current.CancellationToken);

            Assert.True(ToolErrorResult.IsError(result));
            Assert.Equal("lookup_order", result[ToolErrorResult.ToolProperty]!.GetValue<string>());
            Assert.Contains(
                "the order is already closed.",
                result[ToolErrorResult.MessageProperty]!.GetValue<string>(),
                StringComparison.Ordinal);
        }

        // A fault the model cannot answer still propagates, so the framework's own consecutive-
        // error budget (MaximumConsecutiveErrorsPerRequest = 3) counts it and the 4th round throws.
        [Fact]
        public async Task AFaultTheModelCannotAnswer_PropagatesAndSpendsTheConsecutiveErrorBudget()
        {
            TimeoutException failure = new("the endpoint did not answer.");
            ThrowingDeclaredTool tool = new(LookupOrder, failure);

            Exception thrown = await RunUntilTheBudgetThrowsAsync(tool, TestContext.Current.CancellationToken);

            // The very exception, and not a copy: the framework rethrows it by ExceptionDispatchInfo
            // when the budget runs out.
            Assert.Same(failure, thrown);
        }

        // ---------------------------------------------------------------------------------------
        // 5. Stacks are preserved: the middleware uses an exception FILTER for the one case that must
        // never be caught (caller cancellation) and a bare `throw;` for the propagating case, so the
        // stack a beyond-the-model fault carries out still names the tool body that threw it.
        // ---------------------------------------------------------------------------------------
        [Fact]
        public async Task AFaultTheModelCannotAnswer_KeepsItsOriginalStack()
        {
            TimeoutException failure = new("the endpoint did not answer.");
            ThrowingDeclaredTool tool = new(LookupOrder, failure);

            Exception thrown = await RunUntilTheBudgetThrowsAsync(tool, TestContext.Current.CancellationToken);

            Assert.NotNull(thrown.StackTrace);
            Assert.Contains(nameof(ThrowingDeclaredTool), thrown.StackTrace, StringComparison.Ordinal);
            Assert.Contains("CallAsync", thrown.StackTrace, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------
        // 1. Caller cancellation passes through untouched. The TOKEN decides, never the exception
        // type.
        // ---------------------------------------------------------------------------------------
        [Fact]
        public async Task ACallerThatHungUp_PassesTheCancellationThrough()
        {
            TurnInvocation turn = new() { ConversationId = "conversation", TurnIndex = 0, Stage = "" };
            using CancellationTokenSource source = new();

            // The tool cancels the very token the call was made with and then throws, exactly as a
            // caller hanging up looks from inside a tool body: the token and the exception type agree.
            CancelingDeclaredTool tool = new(LookupOrder, source);
            LoopingToolCallingChatClient inner = new();
            using AuditingFunctionInvokingChatClient client = new(inner);
            ChatOptions options = new()
            {
                Tools = [tool],
                AdditionalProperties = new AdditionalPropertiesDictionary { [TurnInvocation.ArgumentsKey] = turn },
            };

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "where is my order")],
                    options,
                    source.Token));
        }

        // A plain AIFunctionFactory.Create(...) tool is not a DeclaredTool, yet its faults are classified
        // like a DeclaredTool's: it gets the identical answerable-fault treatment. See ThrowingToolBuilder in
        // RuntimeFakes.cs, which throws straight at the framework.
        [Fact]
        public async Task APlainAIFunctionFactoryTool_GetsTheSameErrorResultAsADeclaredTool()
        {
            Func<string> body = () => throw new InvalidOperationException("the order is already closed.");
            AIFunction tool = AIFunctionFactory.Create(body, "lookup_order", "Read one order by its identifier.");

            JsonObject result = await RunSingleRoundAsync(tool, TestContext.Current.CancellationToken);

            Assert.True(ToolErrorResult.IsError(result));
            Assert.Equal("lookup_order", result[ToolErrorResult.ToolProperty]!.GetValue<string>());
            Assert.Contains(
                "the order is already closed.",
                result[ToolErrorResult.MessageProperty]!.GetValue<string>(),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task ARealBindingTool_GetsTheSameErrorResultThroughTheRealMiddleware()
        {
            ToolBindingRegistry registry = new();
            _ = registry.Register("CreateCase", (arguments, cancellationToken)
                => throw new InvalidOperationException("the case system is down"));
            ToolConfiguration createCase = new()
            {
                Id = "create_case",
                Kind = ToolKind.Binding,
                Binds = "CreateCase",
                Description = "Open a service case for a human agent.",
            };
            BindingToolSource source = new(registry);
            IReadOnlyList<ToolRegistration> registrations = await source.ProvideAsync(
                new ToolSourceContext(new AgentCoreConfiguration { ApiVersion = "agentcore/v1", Agents = new AgentsConfiguration { Items = [] }, Entries = new Dictionary<string, EntryConfiguration>(), Tools = [createCase] }),
                TestContext.Current.CancellationToken);
            AIFunction tool = Assert.IsType<AIFunction>(Assert.Single(registrations).Materialise(), exactMatch: false);

            JsonObject result = await RunSingleRoundAsync(tool, TestContext.Current.CancellationToken);

            Assert.True(ToolErrorResult.IsError(result));
            Assert.Equal("create_case", result[ToolErrorResult.ToolProperty]!.GetValue<string>());
            Assert.Contains(
                "the case system is down",
                result[ToolErrorResult.MessageProperty]!.GetValue<string>(),
                StringComparison.Ordinal);
        }

        /// <summary>Runs one request/response round and returns the JSON the tool result carried.</summary>
        /// <param name="tool">The tool the fake model calls.</param>
        /// <param name="cancellationToken">Cancels the conversation.</param>
        /// <param name="arguments">
        /// The arguments the fake model fills, or <see langword="null"/> for none — enough for a tool
        /// that validates its own arguments before it ever reaches its adapter.
        /// </param>
        private static async Task<JsonObject> RunSingleRoundAsync(
            AIFunction tool,
            CancellationToken cancellationToken,
            Dictionary<string, object?>? arguments = null)
        {
            ToolCallingChatClient inner = new("the loop continues.", arguments);
            using AuditingFunctionInvokingChatClient client = new(inner);
            ChatOptions options = new() { Tools = [tool] };

            _ = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "where is my order")],
                options,
                cancellationToken);

            string raw = Assert.Single(inner.ToolResults);
            return Assert.IsType<JsonObject>(JsonNode.Parse(raw));
        }

        /// <summary>
        /// Runs rounds until the framework's own <c>MaximumConsecutiveErrorsPerRequest</c> budget throws,
        /// and returns what it threw.
        /// </summary>
        private static async Task<Exception> RunUntilTheBudgetThrowsAsync(AIFunction tool, CancellationToken cancellationToken)
        {
            LoopingToolCallingChatClient inner = new();
            using AuditingFunctionInvokingChatClient client = new(inner);
            ChatOptions options = new() { Tools = [tool] };

            return await Assert.ThrowsAnyAsync<Exception>(() =>
                client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "where is my order")],
                    options,
                    cancellationToken));
        }

        /// <summary>A <see cref="DeclaredTool"/> whose body throws whatever the test hands it.</summary>
        private sealed class ThrowingDeclaredTool(ToolConfiguration tool, Exception failure) : DeclaredTool(tool)
        {
            private readonly Exception _failure = failure;

            protected override ValueTask<object?> CallAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
            {
                throw _failure;
            }
        }

        /// <summary>A <see cref="DeclaredTool"/> whose body cancels the caller's own token and then throws.</summary>
        private sealed class CancelingDeclaredTool(ToolConfiguration tool, CancellationTokenSource source) : DeclaredTool(tool)
        {
            private readonly CancellationTokenSource _source = source;

            protected override ValueTask<object?> CallAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
            {
                _source.Cancel();
                throw new OperationCanceledException();
            }
        }
    }
}
