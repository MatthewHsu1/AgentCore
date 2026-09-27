using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge
{
    /// <summary>What every <c>KnowledgeProviderFactory</c> test class shares: building the turn, the
    /// provider, the framework's own invoke shapes, and the stub session/agent pair none of them run.</summary>
    internal static class KnowledgeProviderFactoryTestSupport
    {
        /// <summary>Builds the provider under test, with the agent id and logger these facts do not read.</summary>
        /// <param name="port">The store the provider searches.</param>
        /// <param name="knowledge">The agent's resolved <c>knowledge:</c> block.</param>
        /// <returns>The provider.</returns>
        internal static TurnInvocation PrefetchTurn(KnowledgeScope? scope = null, TurnSources? sources = null)
        {
            return new()
            {
                ConversationId = "conversation",
                TurnIndex = 0,
                Stage = "",
                Knowledge = scope,
                Sources = sources,
            };
        }

        internal static async Task<AIContext> InvokePrefetchAsync(
            AIContextProvider provider, string text, TurnInvocation turn, AgentSession session)
        {
            TurnRegistry.Set(session, turn);
            return await provider.InvokingAsync(
                Invoking(text, session), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        internal static AIContextProvider Provider(IKnowledgeRetrievalPort port, ResolvedKnowledge knowledge)
        {
            return KnowledgeProviderFactory.Create(
                        port, knowledge, "agent-under-test", new SourceLocatorCitationFormatter(), loggers: null);
            }

        /// <summary>Every message text of a returned context, in one string.</summary>
        internal static string Merged(AIContext context)
        {
            return string.Join('\n', Texts(context));
        }

        internal static IEnumerable<string> Texts(AIContext context)
        {
            return (context.Messages ?? []).Select(message => message.Text);
        }

        internal static ResolvedKnowledge Resolved(
            KnowledgeMode mode, int limit = 5, bool citations = false, bool scoped = false)
        {
            return new(mode, limit, citations, scoped);
        }

        internal static KnowledgeCard Card(string id)
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

        /// <summary>A card <c>see_also</c> pulled in. The store appends these after every scored card.</summary>
        internal static KnowledgeCard Linked(string id)
        {
            return Card(id) with { Score = null, ViaLink = true };
        }

        /// <summary>Runs the provider the way the framework runs it, over one caller message.</summary>
        internal static AIContextProvider.InvokingContext Invoking(string text, AgentSession session)
        {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            return new(
                StubAgent.Instance,
                session,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, text)] });
#pragma warning restore MAAI001
        }

        internal sealed class StubSession : AgentSession;

        /// <summary>Stands in for the agent the framework names on a context. Nothing here runs it.</summary>
        internal sealed class StubAgent : AIAgent
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
    }
}
