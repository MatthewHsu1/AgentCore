using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Shipped;

/// <summary>
/// Turns one shipped agent into the function the outer agent calls.
/// </summary>
/// <remarks>
/// <para>
/// The agent goes onto the same <c>AsAIFunction()</c> path <c>kind: agent</c> already uses, so
/// there is one code path for every agent-as-tool. The inner agent runs on a session of its own
/// that no <c>BeginCall</c> ever names, so none of its rounds reach store 1 —
/// <c>InnerAgentTranscriptTests</c> holds that.
/// </para>
/// <para>
/// The pipeline is deliberately thinner than the one <c>ConfigurationCompiler</c> gives a document
/// agent: client-level telemetry and a plain <c>UseFunctionInvocation</c>, with no
/// <c>AuditingFunctionInvokingChatClient</c> and no agent-level span. The auditing loop exists to
/// turn an inner tool's exception into a section 8.7 result, and the only shipped agent today is
/// <c>ui.draw</c>, whose one inner tool never throws. The first shipped agent whose inner tools call
/// ports that can throw — <c>knowledge.agent_search</c> is next — must move onto the auditing loop,
/// because otherwise that exception ends the outer turn.
/// </para>
/// </remarks>
internal static class ShippedAgentBuilder
{
    /// <summary>Builds the function one shipped agent is advertised as.</summary>
    /// <param name="definition">The shipped agent.</param>
    /// <param name="tool">The declaration the document holds.</param>
    /// <param name="ports">The adapters the host bound.</param>
    /// <returns>The function.</returns>
    /// <exception cref="ConfigurationLoadException">A port this agent reads is unbound.</exception>
    internal static AIFunction Build(
        IShippedAgentDefinition definition, ToolConfiguration tool, BuiltinToolPorts ports)
    {
        if (definition.MissingPort(ports) is { } missing)
        {
            throw BuiltinToolSource.Unbound(tool, definition.Name, missing);
        }

        if (ports.ChatClients is not { } clients)
        {
            throw ToolSourceError.Fail(
                $"the tool '{tool.Id}' is kind: builtin and uses: '{definition.Name}', which is an agent and "
                + "reads IChatClientFactory, and no adapter binds that port. Call options.UseChatClients(...).");
        }

        var described = BuiltinToolSource.Described(tool, definition);
        var rounds = tool.MaxRounds ?? definition.DefaultMaxRounds;

        var agent = new ChatClientAgent(
            clients.GetChatClient(tool.Model)
                   .AsBuilder()
                   .UseFunctionInvocation(configure: invoking => invoking.MaximumIterationsPerRequest = rounds)
                   .UseOpenTelemetry(configure: static client => client.EnableSensitiveData = false)
                   .Build(),
            new ChatClientAgentOptions
            {
                Name = tool.Id,
                ChatOptions = new ChatOptions
                {
                    Instructions = definition.Instructions,
                    Tools = [.. definition.InnerTools(tool, ports)],
                    ToolMode = ChatToolMode.RequireAny,
                },
            });

        return new SpentRoundsAreAnError(
            agent.AsAIFunction(new AIFunctionFactoryOptions
            {
                Name = tool.Id,
                Description = described.Description!,
                ExcludeResultSchema = true,
            }),
            tool.Id,
            rounds);
    }

    /// <summary>
    /// Answers the outer agent a section 8.7 error when the inner agent finished with no words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AsAIFunction</c> hands back the inner agent's final text. Reaching
    /// <c>MaximumIterationsPerRequest</c> costs <c>FunctionInvokingChatClient</c> one further
    /// request with the tools removed, so a model willing to answer in words still gets the last
    /// word and those words ride back. A model that asks for a tool even then leaves a response
    /// whose only content is the call it refused to invoke, and its text arrives here as a
    /// <c>JsonElement</c> of kind <c>String</c> holding <c>""</c>. Handed that, the outer agent
    /// cannot tell a spent round cap from a success and will tell the caller the work is done.
    /// </para>
    /// <para>
    /// The signal available is the empty text, not the cap itself: MEAI surfaces no "I stopped
    /// early" flag on the response. An inner agent that legitimately ends with no words is
    /// therefore reported the same way, which is right — it has given the outer agent nothing to
    /// use either. Whatever the inner agent did find comes back as its text, and text is never
    /// replaced.
    /// </para>
    /// </remarks>
    private sealed class SpentRoundsAreAnError : DelegatingAIFunction
    {
        private readonly string _toolId;
        private readonly int _rounds;

        internal SpentRoundsAreAnError(AIFunction inner, string toolId, int rounds)
            : base(inner)
        {
            _toolId = toolId;
            _rounds = rounds;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var result = await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);

            return HasText(result)
                ? result
                : ToolErrorResult.Create(
                    _toolId,
                    $"'{_toolId}' used all {_rounds} of its rounds and finished with nothing to report, so "
                    + "assume none of it happened. Tell the caller in words, or ask again for something simpler.");
        }

        /// <summary>Reports whether one <c>AsAIFunction</c> result carries words the outer agent can read.</summary>
        private static bool HasText(object? result)
            => result switch
            {
                null => false,
                string text => !string.IsNullOrWhiteSpace(text),
                JsonElement { ValueKind: JsonValueKind.String } element
                    => !string.IsNullOrWhiteSpace(element.GetString()),
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => false,
                _ => true,
            };
    }
}
