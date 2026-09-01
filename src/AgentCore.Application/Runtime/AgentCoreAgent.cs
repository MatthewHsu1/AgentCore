using AgentCore.Application.Calls;
using AgentCore.Application.Ports;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The whole turn loop of this library, behind the framework's own agent abstraction.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CallSession"/> is the turn loop — moderation, the stage machine, the writers, the audit
/// chain, barge-in — and its seam is <see cref="Ports.IConversationPort"/>, which only this library's
/// own adapters read. This shim puts the same loop behind <see cref="AIAgent"/>, the seam the wider
/// ecosystem reads: anything that drives an <c>AIAgent</c> — the framework's protocol hosts (A2A,
/// AG-UI, the OpenAI-compatible endpoints, all prerelease as of Microsoft.Agents.AI 1.17.0),
/// <c>AsAIFunction()</c>, an evaluation harness — can drive a call without learning one AgentCore
/// type beyond this one.
/// </para>
/// <para>
/// One <see cref="AgentSession"/> is one call. <see cref="AIAgent.CreateSessionAsync(CancellationToken)"/>
/// starts the call, and every run with that session is one turn of it. The session wraps the
/// <see cref="CallSession"/>, and a host that needs the parts of the loop the <c>AIAgent</c> surface
/// does not carry — <see cref="CallSession.Interrupt"/>,
/// <see cref="CallSession.EndCall(Domain.Audit.CallEndReason)"/>, the stage,
/// the transcript — asks the session for it: <c>session.GetService&lt;CallSession&gt;()</c>.
/// </para>
/// <para>
/// <b>The session owns the transcript, so a run takes one user message and not a history.</b> The
/// run reads the text of the LAST user message and ignores everything in front of it, which is the
/// same rule the <c>/v1/chat/completions</c> endpoint applies to its request body: an earlier message
/// of the request is already in the call. A host must not replay history here — replaying it would
/// put every turn in the transcript twice.
/// </para>
/// <para>
/// <see cref="AgentRunOptions"/> is accepted and ignored. The compiled document owns the model, the
/// tools, and the response shape, and the framework's contract lets an implementation ignore options
/// it cannot honor.
/// </para>
/// <para>
/// This agent is a process singleton, like the compiled agent it runs: everything per call lives in
/// the session. It holds no lock and no state of its own, so 26 concurrent calls are 26 sessions
/// over one instance.
/// </para>
/// </remarks>
public sealed class AgentCoreAgent : AIAgent
{
    private readonly ICallSessionFactory _sessions;

    private readonly string? _name;
    
    private readonly string? _description;

    /// <summary>Creates the shim over one compiled document's turn loop.</summary>
    /// <param name="sessions">The factory that starts one <see cref="CallSession"/> for each call.</param>
    /// <param name="name">The name the agent reports, usually the document name, or <see langword="null"/>.</param>
    /// <param name="description">The description the agent reports, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is <see langword="null"/>.</exception>
    public AgentCoreAgent(ICallSessionFactory sessions, string? name = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
        _name = name;
        _description = description;
    }

    /// <inheritdoc />
    public override string? Name => _name;

    /// <inheritdoc />
    public override string? Description => _description;

    /// <summary>Starts one call under the id the host names.</summary>
    /// <param name="callId">The id the host gives the call. The vendor's call id belongs here.</param>
    /// <param name="cancellationToken">Unused. The signature mirrors the framework's.</param>
    /// <returns>The session of the new call, with no turn run yet.</returns>
    /// <remarks>
    /// <see cref="AIAgent.CreateSessionAsync(CancellationToken)"/> makes an id up, exactly as
    /// <see cref="ICallSessionFactory.Create"/> does when it is handed none. A voice host has the
    /// vendor's call id before the first turn, and the audit chain of the call should carry it, so
    /// this overload exists for the same reason <c>ChatClientAgent.CreateSessionAsync(string)</c>
    /// does.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="callId"/> is null or empty.</exception>
    public ValueTask<AgentSession> CreateSessionAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        return new(new AgentCoreAgentSession(_sessions.Create(callId)));
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => new(new AgentCoreAgentSession(_sessions.Create()));

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// What travels is the call's id and what its session alone holds: the stage, whether the
    /// machine finished, and the slots the writers filled. That is a <see cref="CallSessionState"/>
    /// under the id it belongs to — the same pairing <c>ChatClientAgentSession</c> writes as
    /// <c>{ conversationId, stateBag }</c>, and for the same reason. State that travelled without
    /// its id would come back on a call that has none of its words behind it.
    /// </para>
    /// <para>
    /// <b>The words are not here.</b> They are store 1, which is durable already, so a checkpoint
    /// that carried them would give one conversation two records and one chance to disagree. The
    /// price is that this is a key and not a copy: a host moving a session between processes still
    /// needs the shared store behind it, or the revived call answers with an empty transcript.
    /// </para>
    /// <para>
    /// <b>This blob is not store 0's.</b> A host's <paramref name="jsonSerializerOptions"/> reach
    /// the nested <c>state</c> member too, not the envelope alone, so that member is encoded to the
    /// host's rules and not to <see cref="CallStateJson.Options"/>, which every store agrees on. It
    /// is harmless while the member stays inside the envelope, because this seam and its reader are
    /// symmetric. Lifting it out and writing it to store 0 is what would not be.
    /// </para>
    /// </remarks>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        // Refused, where a run answers a null session by making one. The fabricated call is the
        // framework's one-shot shape for a RUN — one turn of a call nothing continues — and there is
        // no such thing as a one-shot serialize: what it would write is an envelope naming a fresh
        // random id beside an empty state, and a host would keep that as its checkpoint and never
        // learn it points at nothing. The parameter is not nullable, so this only catches a caller
        // that went around the type.
        ArgumentNullException.ThrowIfNull(session);

        var call = Resolve(session);

        return new(JsonSerializer.SerializeToElement(
            new SerializedSession(call.CallId, call.Snapshot()),
            jsonSerializerOptions ?? CallStateJson.Options));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reads back what <see cref="SerializeSessionCoreAsync"/> wrote — see its remarks for what the
    /// blob does and does not carry. It is host-facing, so what arrives is not necessarily anything
    /// this library wrote, and it stays lenient about that: a blob that names no call gets a new id,
    /// exactly as <see cref="AIAgent.CreateSessionAsync(CancellationToken)"/> does, and state the
    /// document no longer allows is dropped slot by slot with a diagnostic rather than refusing the
    /// call. That second contract is <see cref="CallSession.Resume"/>'s and this method does not
    /// double it.
    /// </para>
    /// <para>
    /// The one blob it refuses carries neither member. That is not a validation layer over
    /// <see cref="CallSession.Resume"/> but a guard against one specific confusion this library
    /// creates itself: a bare <see cref="CallSessionState"/> is the literal value in store 0's
    /// <c>call.state</c> column, so a host reaching for "the state of this call" reaches for the
    /// wrong one of two shapes. Read as an envelope it names no call and holds no state, and the
    /// failure is silent and total — a new random id, and the transcript orphaned behind it.
    /// <see cref="SerializeSessionCoreAsync"/> can never write that blob, so refusing it costs the
    /// lenient path nothing.
    /// </para>
    /// <para>
    /// <b>Store 0 outranks what arrives here.</b> The state travels with the call's id, and the
    /// first turn of the revived call reads store 0 under that id; where store 0 holds state of its
    /// own it wins outright, because its blob rides the same batch as the words. So this decides
    /// only a call store 0 does not know, or knows without state. See
    /// <see cref="CallSession.Resume"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="serializedState"/> names no call and holds no state.
    /// </exception>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var stored = serializedState.Deserialize<SerializedSession>(
            jsonSerializerOptions ?? CallStateJson.Options);

        if (stored is null or { CallId: null, State: null })
        {
            throw new ArgumentException(
                "The serialized session names no call and holds no state, so it is not one this "
                + "agent wrote. It expects { callId, state } — the call's id beside its state. A "
                + "bare CallSessionState, the value store 0 keeps in call.state, is the other shape "
                + "and reading it as this one would lose the call's transcript.",
                nameof(serializedState));
        }

        return new(new AgentCoreAgentSession(_sessions.Create(stored.CallId, stored.State)));
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var call = Resolve(session);
        var turn = await call.RunTurnAsync(UserText(messages), cancellationToken).ConfigureAwait(false);

        return new AgentResponse(new ChatMessage(ChatRole.Assistant, turn.ReplyText))
        {
            AgentId = Id,
            ResponseId = Guid.NewGuid().ToString("N"),
            CreatedAt = turn.EndedAt,
        };
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var call = Resolve(session);

        // The stream is already filtered: CallSession drops the lifecycle updates, so every update
        // here carries content. The wrap changes the type and nothing else.
        await foreach (var update in call.RunTurnStreamingAsync(UserText(messages), cancellationToken)
            .ConfigureAwait(false))
        {
            yield return new AgentResponseUpdate(update) { AgentId = Id };
        }
    }

    /// <summary>Finds the call a run belongs to.</summary>
    /// <param name="session">The session the caller passed, or <see langword="null"/>.</param>
    /// <returns>The call to run the turn on.</returns>
    /// <remarks>
    /// A <see langword="null"/> session is the framework's one-shot shape — <c>ChatClientAgent</c>
    /// answers it with a session it throws away — so it runs one turn of a call nothing can continue.
    /// A session another agent created is refused, in the framework's own words: every
    /// <c>AIAgent</c> implementation measured (ChatClientAgent, the workflow host) refuses a foreign
    /// session rather than guessing at its state.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="session"/> belongs to another agent type.</exception>
    private CallSession Resolve(AgentSession? session) => session switch
    {
        null => _sessions.Create(),
        AgentCoreAgentSession own => own.Call,
        _ => throw new ArgumentException(
            $"Incompatible session type: {session.GetType()} (expecting {typeof(AgentCoreAgentSession)}). "
            + "Only a session this agent created can carry one of its calls.",
            nameof(session)),
    };

    /// <summary>Reads what the caller said out of the run's messages.</summary>
    /// <param name="messages">The messages the caller passed to the run.</param>
    /// <returns>The text of the last user message.</returns>
    /// <remarks>
    /// The same rule the <c>/v1/chat/completions</c> endpoint applies: the session owns the
    /// transcript, so an earlier message of the request is already in the call, and only the last
    /// user message is new. See the remarks on <see cref="AgentCoreAgent"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">No user message carries text, so there is no turn to run.</exception>
    private static string UserText(IEnumerable<ChatMessage> messages)
    {
        string? text = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User && message.Text is { Length: > 0 } spoken)
            {
                text = spoken;
            }
        }

        return text ?? throw new ArgumentException(
            "The run carries no user message with text, so there is no turn to run. The session owns "
            + "the transcript: pass what the caller just said, not a history.",
            nameof(messages));
    }

    /// <summary>One serialized session: the call it is, and the state it held.</summary>
    /// <param name="CallId">The id of the call. Store 1 is keyed by it, so it is the half that finds the words.</param>
    /// <param name="State">What the session alone held, or <see langword="null"/> when the blob named none.</param>
    /// <remarks>
    /// A separate shape from <see cref="CallSessionState"/> on purpose. That one is the value store 0
    /// writes under a <c>call_id</c> column, so putting the id inside it would give one fact two
    /// homes; here there is no column, so the envelope carries the key beside the value instead.
    /// Internal because it is a wire shape and not a promise: D15 makes every public type permanent.
    /// </remarks>
    internal sealed record SerializedSession(string? CallId, CallSessionState? State);

    /// <summary>One call, as the framework sees it.</summary>
    /// <remarks>
    /// It stays internal: a host reaches the call through <c>GetService&lt;CallSession&gt;()</c> on
    /// the <see cref="AgentSession"/> base, and D15 makes every public type a permanent promise this
    /// wrapper does not need to be.
    /// </remarks>
    internal sealed class AgentCoreAgentSession : AgentSession
    {
        internal AgentCoreAgentSession(CallSession call) => Call = call;

        /// <summary>Gets the call this session carries.</summary>
        internal CallSession Call { get; }

        /// <inheritdoc />
        /// <remarks>
        /// Answers the <see cref="CallSession"/> (and its <see cref="Ports.IConversationPort"/>
        /// seam), so a host holding only the framework types reaches barge-in, the stage, and the
        /// transcript without a cast.
        /// </remarks>
        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceKey is null && serviceType.IsInstanceOfType(Call)
                ? Call
                : base.GetService(serviceType, serviceKey);
        }
    }
}
