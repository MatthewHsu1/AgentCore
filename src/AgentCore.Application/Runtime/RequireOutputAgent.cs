using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Fails a row 4 graph run that produced no text, instead of letting it end quietly.
/// </summary>
internal sealed class RequireOutputAgent : DelegatingAIAgent
{
    /// <summary>The reason a row 4 graph that produced no text reports, after it names the graph.</summary>
    internal const string NoOutputMessage =
        "Row 4 of the section 8.2 compile table ends the run at idle when no outgoing edge guard of "
        + "the node that holds the run is true. Measured on Microsoft.Agents.AI.Workflows 1.17.0: "
        + "InProcessRunner raises no exception for that, and the AsAIAgent() wrapper emits no update, "
        + "so the caller would read an empty run and no error. Read the when: guard of every edge that "
        + "leaves the node the run reached, and make one of them true for this state. A stage says "
        + "onNoMatch: error, and a graph node takes no such key: D15 makes a new public key a "
        + "permanent obligation, so this check adds none.";

    private readonly string _name;

    /// <summary>Puts the output check in front of one compiled graph.</summary>
    /// <param name="inner">The workflow of row 4, already wrapped by <c>AsAIAgent()</c>.</param>
    /// <param name="name">The name of the document, which the failure reports.</param>
    public RequireOutputAgent(AIAgent inner, string name)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(name);
        _name = name;
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.RunCoreAsync(messages, session, options, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(response.Text) ? throw NoOutput() : response;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var carriedText = false;

        await foreach (var update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
            .ConfigureAwait(false))
        {
            carriedText |= !string.IsNullOrWhiteSpace(update.Text);
            yield return update;
        }

        if (!carriedText)
        {
            throw NoOutput();
        }
    }

    private InvalidOperationException NoOutput()
        => new($"The graph '{_name}' produced no text. " + NoOutputMessage);
}
