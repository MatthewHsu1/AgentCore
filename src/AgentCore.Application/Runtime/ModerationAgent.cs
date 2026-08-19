using System.Runtime.CompilerServices;
using AgentCore.Application.Evaluation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Refuses a turn whose caller text the moderation endpoint flagged, before the agent runs.
/// </summary>
/// <remarks>
/// <para>
/// R3: moderation judges the caller, not the reply. The owner decided this on 2026-08-13, over
/// section 11 item 11. Recording a harmful reply protects nobody, because the caller already heard
/// it, so reply moderation is withdrawn and the model carries its own safety training.
/// </para>
/// <para>
/// <b>It fails open.</b> An endpoint that times out or throws lets the turn run, and the layer
/// reports <see cref="ModerationOutcome.Unavailable"/> so an operator can alert on the rate. A
/// vendor outage must not refuse every caller on a support line.
/// </para>
/// <para>
/// <b>It wraps an AGENT and not a chat client, and that altitude is load-bearing.</b> A graph row
/// runs several chat clients for one turn, so a chat-pipeline layer would moderate the caller once
/// per node and hand a refusal to the next node instead of to the caller. One turn is one run of one
/// agent on every row of the compile table, which is the altitude R3 is written at.
/// </para>
/// <para>
/// It sits outside <see cref="FallbackAgent"/>, so the verdict survives a run that then threw: the
/// disposition is attached after the inner run, and this layer folds whatever the fallback layer
/// reported into one marker.
/// </para>
/// <para>
/// It holds no per-call state: one instance is compiled once and shared by every call under T44 and
/// R7.
/// </para>
/// </remarks>
internal sealed class ModerationAgent : DelegatingAIAgent
{
    /// <summary>How long a turn waits for the moderation endpoint before it answers anyway.</summary>
    /// <remarks>
    /// <para>
    /// <b>This bound sits in front of the caller, so it is far tighter than the deadline on the work
    /// after the reply.</b> Moderation reads what the caller said before the model runs, so the
    /// caller hears silence for as long as it takes. Two seconds is already long on a voice call.
    /// The endpoint answers in a fraction of that, and a wait this long means it is not answering at
    /// all.
    /// </para>
    /// <para>
    /// The design permits this wait where it would refuse a judge. Section 9 says "moderation is not
    /// a judge, it is one free HTTP POST", which exempts it from the D9 rule that keeps a judge off
    /// the turn.
    /// </para>
    /// <para>
    /// It is not a document setting: it guards against a slow vendor and is not a product knob, so
    /// it carries no JSON Schema field and no configuration key.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly PromptModerator _moderator;
    private readonly string _refusalReply;
    private readonly TimeSpan _timeout;

    /// <summary>Puts moderation in front of one turn agent.</summary>
    /// <param name="inner">The agent a turn runs.</param>
    /// <param name="moderator">The endpoint seam.</param>
    /// <param name="refusalReply">What a refused turn speaks. Never the fallback line.</param>
    /// <param name="timeout">How long the caller waits for a verdict before the turn runs anyway.</param>
    public ModerationAgent(AIAgent inner, PromptModerator moderator, string refusalReply, TimeSpan timeout)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(moderator);
        ArgumentNullException.ThrowIfNull(refusalReply);

        _moderator = moderator;
        _refusalReply = refusalReply;
        _timeout = timeout;
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var verdict = await JudgeAsync(messages, cancellationToken).ConfigureAwait(false);
        if (verdict.Moderation is ModerationOutcome.Flagged)
        {
            AgentResponse refusal = new(new ChatMessage(ChatRole.Assistant, _refusalReply));
            Attach(refusal.AdditionalProperties ??= [], verdict);
            return refusal;
        }

        var response = await base.RunCoreAsync(messages, session, options, cancellationToken)
            .ConfigureAwait(false);

        Attach(response.AdditionalProperties ??= [], verdict);
        return response;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var verdict = await JudgeAsync(messages, cancellationToken).ConfigureAwait(false);
        if (verdict.Moderation is ModerationOutcome.Flagged)
        {
            AgentResponseUpdate refusal = new(ChatRole.Assistant, _refusalReply);
            Attach(refusal.AdditionalProperties ??= [], verdict);
            yield return refusal;
            yield break;
        }

        // A leading update with EMPTY contents. Empty contents contribute no text, so the caller's
        // audio is untouched and the turn seam drops the update before the host ever sees it.
        // Update-level properties do not survive streaming coalescing, which is why the marker rides
        // an update of its own rather than the assistant message the updates fold into.
        AgentResponseUpdate marker = new() { Role = ChatRole.Assistant };
        Attach(marker.AdditionalProperties ??= [], verdict);
        yield return marker;

        await foreach (var update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>Reads the endpoint's verdict on what the caller said, and never lets it drop a turn.</summary>
    /// <param name="messages">The whole request. The caller's words are its last user message.</param>
    /// <param name="cancellationToken">The host's own token. A cancel it asked for still propagates.</param>
    /// <returns>The verdict, as the disposition the turn loop reads.</returns>
    private async ValueTask<TurnDisposition> JudgeAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var callerText = CallerText(messages);

        IReadOnlyList<string> categories;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(_timeout);
            try
            {
                categories = await _moderator.FlaggedCategoriesAsync(callerText, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The deadline passed, and not the caller's own token. A cancel the host asked for
                // still propagates, because that is the host ending the turn and not a slow vendor.
                return Unavailable(CallSession.ModerationTimedOutReason);
            }
#pragma warning disable CA1031 // Moderation guards the turn. It must never be the thing that drops it.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                return Unavailable(CallSession.ModerationFaultedReason);
            }
        }

        return categories.Count == 0
            ? new TurnDisposition(ModerationOutcome.Clean, null, FallbackCause.None, null, null)

            // The order is the endpoint's, because AuditPayloadKeys.ModerationCategories promises it.
            : new TurnDisposition(
                ModerationOutcome.Flagged, string.Join(',', categories), FallbackCause.None, null, null);
    }

    private static TurnDisposition Unavailable(string reason)
        => new(ModerationOutcome.Unavailable, null, FallbackCause.None, null, reason);

    /// <summary>Reads the words the caller spoke this turn out of the whole request.</summary>
    /// <param name="messages">The request the turn is about to run.</param>
    /// <returns>The text of the last user message, or an empty string when the request holds none.</returns>
    /// <remarks>
    /// The turn loop appends the caller's words as the last message of the request, so the last user
    /// message is what this turn is about and everything before it was already moderated on the turn
    /// that carried it. An unfilled-slot reminder rides that same message, so it is moderated too;
    /// the library wrote it, so it is never what a verdict turns on.
    /// </remarks>
    private static string CallerText(IEnumerable<ChatMessage> messages)
    {
        string? spoken = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                spoken = message.Text;
            }
        }

        return spoken ?? string.Empty;
    }

    /// <summary>Puts the verdict on the run, folding in what the fallback layer already said.</summary>
    /// <param name="properties">Where the turn loop reads the marker from.</param>
    /// <param name="verdict">What moderation decided.</param>
    private static void Attach(AdditionalPropertiesDictionary properties, TurnDisposition verdict)
    {
        if (properties.TryGetValue<TurnDisposition>(out var existing) && existing is not null)
        {
            verdict = verdict with { Fallback = existing.Fallback, FallbackReason = existing.FallbackReason };
            properties.Remove<TurnDisposition>();
        }

        properties.Add(verdict);
    }
}
