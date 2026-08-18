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
    /// Not supported yet. Everything a resumed call needs — the transcript, the state document, the
    /// stage, the event ordinal — lives on <see cref="CallSession"/>, which does not expose its
    /// private state for rehydration today. This shim is the seam that makes call resume across a
    /// process restart possible at all; building it means giving <see cref="CallSession"/> a
    /// serialization surface, which is its own change with its own review, not a rider on this one.
    /// </remarks>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(SerializationNotSupported);

    /// <inheritdoc />
    /// <remarks>Not supported yet, for the reason <see cref="SerializeSessionCoreAsync"/> gives.</remarks>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(SerializationNotSupported);

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

    /// <summary>The message both serialization methods throw with.</summary>
    /// <remarks>Internal so a test asserts the message a host reads, and D15 keeps it off the public surface.</remarks>
    internal const string SerializationNotSupported =
        "This agent cannot serialize a session yet. A call's state lives on CallSession, which has no "
        + "serialization surface today. See the remarks on AgentCoreAgent.SerializeSessionCoreAsync.";

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
