using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Answers a run that threw, or one that spoke no words at all, with the spoken fallback.
/// </summary>
/// <remarks>
/// <para>
/// R1: a tool that fails four times in a row throws out of the run, and section 8.7 says that must
/// never kill the call. R2: at 40 tool rounds the framework sends request 41 with no tools and
/// returns quietly, with no exception — on a voice call that silence is the failure, so an empty
/// reply is read rather than trusting the absence of an error. Both end here, and both leave the
/// turn an ordinary successful run.
/// </para>
/// <para>
/// <b>A cancel is not a fault.</b> <see cref="OperationCanceledException"/> is never swallowed: a
/// barge-in ends the turn on the caller's terms and the turn loop records what was heard.
/// </para>
/// <para>
/// <b>It wraps an AGENT and not a chat client.</b> One turn is one run of one agent on every row of
/// the compile table. A chat-pipeline layer would answer each graph node separately, and a node that
/// went quiet would then speak the fallback INTO the graph — which is exactly the silent graph
/// failure section 8.2 refuses to ship, hidden rather than raised.
/// </para>
/// <para>
/// A run that threw carries no messages out, because the fault ended it. An empty run keeps the
/// messages it did produce, so a tool pair that finished stays visible to the next turn.
/// </para>
/// <para>
/// <b>On a graph row the emptiness that matters is the OUTPUT node's, not the run's.</b> Measured on
/// Microsoft.Agents.AI 1.17.0: a sequential graph whose last node answers with whitespace returns one
/// message, and it is the node BEFORE it. Reading the run as "it produced text" would put the graph's
/// own deliberation in the caller's ear. The constructor's <c>spokenBy</c> set names the agents whose
/// reply the caller hears, so a quiet output node reads as the silence it is.
/// </para>
/// <para>
/// It holds no per-call state: one instance is compiled once and shared by every call under T44 and
/// R7.
/// </para>
/// </remarks>
internal sealed class FallbackAgent : DelegatingAIAgent
{
    private readonly string _fallbackReply;
    private readonly IReadOnlySet<string>? _spokenBy;

    /// <summary>Puts the fallback in front of one turn agent.</summary>
    /// <param name="inner">The agent a turn runs.</param>
    /// <param name="fallbackReply">What a failed turn speaks.</param>
    /// <param name="spokenBy">
    /// The <c>agents.items</c> ids whose reply the caller hears, or <see langword="null"/> when the
    /// last thing the run produced is that reply. It is null for rows 1 and 2, which run one agent,
    /// and for the graph patterns where any participant may answer last.
    /// </param>
    public FallbackAgent(AIAgent inner, string fallbackReply, IReadOnlySet<string>? spokenBy = null)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(fallbackReply);
        _fallbackReply = fallbackReply;
        _spokenBy = spokenBy;
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AgentResponse response;
        try
        {
            response = await base.RunCoreAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Section 8.7, row six: the run throws, the turn ends, and the call lives.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            AgentResponse spoken = new(new ChatMessage(ChatRole.Assistant, _fallbackReply));
            spoken.AdditionalProperties ??= [];
            spoken.AdditionalProperties.Add(Disposition(FallbackCause.Faulted, exception.Message));
            return spoken;
        }

        if (!string.IsNullOrWhiteSpace(SpokenText(response)))
        {
            return response;
        }

        response.Messages.Add(new ChatMessage(ChatRole.Assistant, _fallbackReply));
        response.AdditionalProperties ??= [];
        response.AdditionalProperties.Add(Disposition(FallbackCause.EmptyReply, null));
        return response;
    }

    /// <inheritdoc />
    /// <remarks>
    /// C# forbids <c>yield return</c> inside a <c>catch</c> (CS1631), so the inner enumerator is
    /// driven by hand: the fault goes to a local, and the fallback is emitted after the try/catch.
    /// </remarks>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var updates = base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        var spokeText = false;
        var cause = FallbackCause.None;
        string? reason = null;

        try
        {
            while (true)
            {
                AgentResponseUpdate? current = null;
                try
                {
                    if (await updates.MoveNextAsync().ConfigureAwait(false))
                    {
                        current = updates.Current;
                    }
                }
#pragma warning disable CA1031 // Section 8.7, row six. See the buffered path above.
                catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    cause = FallbackCause.Faulted;
                    reason = exception.Message;
                }

                if (cause is not FallbackCause.None || current is null)
                {
                    break;
                }

                spokeText = spokeText || (Speaks(current.AuthorName) && !string.IsNullOrWhiteSpace(current.Text));
                yield return current;
            }
        }
        finally
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }

        if (cause is FallbackCause.None && !spokeText)
        {
            cause = FallbackCause.EmptyReply;
        }

        if (cause is not FallbackCause.None)
        {
            AgentResponseUpdate spoken = new(ChatRole.Assistant, _fallbackReply);
            spoken.AdditionalProperties ??= [];
            spoken.AdditionalProperties.Add(Disposition(cause, reason));
            yield return spoken;
        }
    }

    /// <summary>Reads the words this run would put in the caller's ear.</summary>
    /// <param name="response">What the inner agent answered.</param>
    /// <returns>The spoken text, or an empty string when nothing the caller hears was produced.</returns>
    private string SpokenText(AgentResponse response) => SpokenReply.From(response.Messages, _spokenBy);

    /// <summary>Reads whether the caller hears what this author said.</summary>
    /// <param name="authorName">The node that produced the message or the update, if any.</param>
    /// <returns><see langword="true"/> when the text counts as the reply.</returns>
    private bool Speaks(string? authorName)
        => _spokenBy is null || (authorName is not null && _spokenBy.Contains(authorName));

    /// <summary>
    /// What this layer alone knows. <see cref="ModerationAgent"/> folds its own verdict in above.
    /// </summary>
    private static TurnDisposition Disposition(FallbackCause cause, string? reason)
        => new(Moderation: null, FlaggedCategories: null, cause, reason, ModerationReason: null);
}
