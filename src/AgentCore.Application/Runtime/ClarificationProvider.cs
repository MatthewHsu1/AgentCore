using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Channel 1 of the ambiguity design (§7): the clarification, riding as a turn instruction. Bound
/// beside <see cref="TurnContextProvider"/> on every compiled agent whose document declares
/// <c>providers.knowledge.ambiguity</c>.
/// </summary>
/// <remarks>
/// Uses the same session guard as <see cref="TurnContextProvider"/>: when
/// <see cref="TurnContextScope.For"/> returns <see langword="null"/> for the run's own session, this
/// is not the caller's own turn — a delegated <c>kind: agent</c> run, or a participant of a
/// <c>graph:</c> row whose session never carries the call's own history — and the channel emits
/// nothing and counts nothing (K29, K39). Because <see cref="AIContextProvider"/>s run once per
/// <c>RunAsync</c>, above the framework's tool-call loop, this also means a nested tool call never
/// reaches this type at all; the K42 strip that hides <see cref="Clarifications"/> from a nested
/// search exists for the probe, not for this provider.
/// </remarks>
internal sealed class ClarificationProvider : AIContextProvider
{
    private readonly KnowledgeAmbiguityConfiguration _ambiguity;

    private readonly IReadOnlyList<string> _fromState;

    private readonly IReadOnlyDictionary<string, string?> _slotDescriptions;

    /// <summary>Creates the provider for one document's <c>providers.knowledge</c> block.</summary>
    /// <param name="ambiguity">How many candidates and how many asks the document allows.</param>
    /// <param name="fromState">
    /// The scope's <c>fromState</c> slots, in declaration order — the same order §8 step 4 walks, so
    /// a deployer reads one ordering rather than two.
    /// </param>
    /// <param name="slotDescriptions">
    /// Each <paramref name="fromState"/> slot's <c>description</c>, or <see langword="null"/> for a
    /// slot the document describes with nothing but its own name.
    /// </param>
    internal ClarificationProvider(
        KnowledgeAmbiguityConfiguration ambiguity,
        IReadOnlyList<string> fromState,
        IReadOnlyDictionary<string, string?> slotDescriptions)
    {
        ArgumentNullException.ThrowIfNull(ambiguity);
        ArgumentNullException.ThrowIfNull(fromState);
        ArgumentNullException.ThrowIfNull(slotDescriptions);

        _ambiguity = ambiguity;
        _fromState = fromState;
        _slotDescriptions = slotDescriptions;
    }

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // K38: a deployment that configured itself never to ask must never reach the loop below —
        // at maxAsks 0 or below, step 2's namedAsks < maxAsks test is false on the very first slot,
        // which would otherwise let step 3's reset ask exactly one question in the one deployment
        // that asked for none.
        if (_ambiguity.MaxAsks <= 0 || TurnContextScope.For(context.Session) is null)
        {
            return new(new AIContext());
        }

        var ambients = TurnAmbients.Current;
        if (ambients?.Clarifications is not { } clarifications || ambients.State is not { } state)
        {
            return new(new AIContext());
        }

        List<ChatMessage> messages = [];

        foreach (var slot in _fromState)
        {
            if (!state.IsUnfilled(slot))
            {
                continue;
            }

            var snapshot = clarifications.Read(slot);
            if (snapshot.Pending is not { Count: > 0 } pending)
            {
                continue;
            }

            if (Speak(clarifications, slot, pending, snapshot, first: messages.Count == 0) is { } text)
            {
                messages.Add(new ChatMessage(ChatRole.System, text));
            }
        }

        return new(new AIContext { Messages = messages.Count > 0 ? messages : null });
    }

    /// <summary>§7's four-step evaluation for one pending slot.</summary>
    /// <returns>The sentence to speak, or <see langword="null"/> when this slot stays silent.</returns>
    private string? Speak(
        Clarifications clarifications,
        string slot,
        IReadOnlyList<string> pending,
        Clarifications.SlotSnapshot snapshot,
        bool first)
    {
        var wouldName = Clarifications.LastNamed.For(pending, _ambiguity.MaxCandidates);

        // §7 step 1 (K37): the caller has already been asked exactly this.
        if (wouldName.Names(snapshot.LastNamed))
        {
            return null;
        }

        var description = ClarificationText.DescriptionOf(slot, _slotDescriptions);

        if (snapshot.NamedAsks < _ambiguity.MaxAsks)
        {
            clarifications.Ask(slot, wouldName, spendsReset: false);
            return ClarificationText.Instruction(description, pending, _ambiguity.MaxCandidates, first);
        }

        // §7 step 3: namedAsks is at the cap and, having failed step 1, what would be named has
        // changed. The one reset is spent here.
        if (!snapshot.ResetSpent)
        {
            clarifications.Ask(slot, wouldName, spendsReset: true);
            return ClarificationText.Instruction(description, pending, _ambiguity.MaxCandidates, first);
        }

        return null;
    }
}
