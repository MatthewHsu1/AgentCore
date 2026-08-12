using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Policy;
using AgentCore.Application.State;
using AgentCore.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The turn loop of one call. It owns the state, the stage machine, and the transcript.
/// </summary>
/// <remarks>
/// <para>
/// The compiled agent is a process singleton, so nothing per call may live on it. Everything per call
/// lives here: one <see cref="StateDocument"/>, one <see cref="StagePolicy"/>, and one transcript.
/// One session belongs to one call and runs one turn at a time, so it takes no lock over its state.
/// </para>
/// <para>
/// The session owns the transcript rather than an agent-bound session object. A <c>policy:</c>
/// document switches the <c>AIAgent</c> between stages, and a conversation bound to one agent cannot
/// carry a call that changes agent. The turn loop therefore passes the accumulated messages into each
/// run, and every stage reads the whole call.
/// </para>
/// <para>
/// The writers run in one fixed order, and every turn repeats it:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="ConstStateWriter"/>, once, when the session is created. A constant never changes.
/// </description></item>
/// <item><description>
/// <see cref="ToolStateWriter"/>, for each tool result the turn produced, in the order the agent
/// produced them. A tool ran inside the turn, so its answer is older than the reply.
/// </description></item>
/// <item><description>
/// <see cref="StateExtractor"/>, after the reply completes, because <c>extractor.when:
/// after_reply</c> reads the finished turn. It runs after the tool writer so the model cannot
/// overwrite a fact a tool already reported for the same turn.
/// </description></item>
/// <item><description>
/// The reserved slots: <c>turnIndex</c> and <c>callDurationSeconds</c>. They move before the counters
/// so a counter rule and an exit guard read the same turn. <c>turnIndex</c> counts the finished
/// turns, so it names the turn that runs next, while <see cref="TurnResult.TurnIndex"/> names the
/// turn that just ran.
/// </description></item>
/// <item><description>
/// <see cref="CounterStateWriter"/>, which reads one snapshot of everything above.
/// </description></item>
/// </list>
/// <para>
/// Only then does <see cref="StagePolicy.Advance"/> run. The stage the machine holds while the
/// writers run is still the stage the turn spoke in, which is what a rule such as
/// <c>{ "===": [ { var: stage }, "resolve" ] }</c> means.
/// </para>
/// </remarks>
public sealed class CallSession
{
    private readonly CompiledAgent _compiled;
    private readonly StagePolicy? _policy;
    private readonly StateExtractor? _extractor;
    private readonly CounterStateWriter _counters;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedAt;
    private readonly List<ChatMessage> _transcript = [];
    private int _running;

    /// <summary>Creates the session of one call.</summary>
    /// <param name="callId">The id of the call.</param>
    /// <param name="compiled">The compiled agent. It is shared by every call.</param>
    /// <param name="guards">The evaluator that runs each exit guard and each increment rule.</param>
    /// <param name="extractor">The extractor, or <see langword="null"/> when the document declares none.</param>
    /// <param name="timeProvider">The clock the reserved <c>callDurationSeconds</c> slot reads.</param>
    internal CallSession(
        string callId,
        CompiledAgent compiled,
        IGuardEvaluator guards,
        StateExtractor? extractor,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(timeProvider);

        CallId = callId;
        _compiled = compiled;
        _extractor = extractor;
        _counters = new CounterStateWriter(guards);
        _time = timeProvider;
        _startedAt = timeProvider.GetUtcNow();

        // A document with no policy: has no stage machine. The single-agent row and both graph rows
        // read that way, and neither of them ever ends a call by itself.
        _policy = compiled.Configuration.Policy is null ? null : compiled.CreatePolicy(guards);
        State = new StateDocument(compiled.Configuration, _policy?.Stage);

        // Writer order, step 1.
        ConstStateWriter.Apply(State);
    }

    /// <summary>Gets the id of the call.</summary>
    public string CallId { get; }

    /// <summary>Gets the stage the machine holds. It is empty when the document declares no policy.</summary>
    public string Stage => State.Stage;

    /// <summary>Gets whether the call reached a terminal stage. A document with no policy never does.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Gets the state of this call. Every guard and every increment rule reads it.</summary>
    public StateDocument State { get; }

    /// <summary>Gets the conversation, oldest first. The session owns it, and every stage reads it.</summary>
    public IReadOnlyList<ChatMessage> Transcript => _transcript;

    /// <summary>Gets the turn that finished last, or <see langword="null"/> before the first turn ends.</summary>
    public TurnResult? LastTurn { get; private set; }

    /// <summary>Gets the compiled agent this session runs. Every call shares it.</summary>
    public CompiledAgent Compiled => _compiled;

    /// <summary>Runs one turn end to end, and returns what it did.</summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The finished turn.</returns>
    /// <exception cref="InvalidOperationException">
    /// The call already reached a terminal stage, another turn of this call is still running, or the
    /// stage the machine holds names no agent.
    /// </exception>
    public async Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        var turn = BeginTurn(userInput);
        try
        {
            var response = await turn.Agent
                .RunAsync(turn.Request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            LastTurn = await CompleteTurnAsync(turn, response, cancellationToken).ConfigureAwait(false);
            return LastTurn;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Runs one turn and streams the reply as it arrives.</summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The reply, one update at a time.</returns>
    /// <remarks>
    /// The turn finishes when the enumeration finishes. After that, <see cref="LastTurn"/> holds the
    /// finished turn, the writers have run, and the machine holds the stage of the next turn. A
    /// caller that stops enumerating early stops the turn, and the state does not move.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The call already reached a terminal stage, another turn of this call is still running, or the
    /// stage the machine holds names no agent.
    /// </exception>
    public async IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        var turn = BeginTurn(userInput);
        try
        {
            List<AgentResponseUpdate> updates = [];

            await foreach (var update in turn.Agent
                .RunStreamingAsync(turn.Request, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);

                // The host speaks this, so it leaves the seam as fast as the model produced it.
                yield return update.AsChatResponseUpdate();
            }

            LastTurn = await CompleteTurnAsync(turn, updates.ToAgentResponse(), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Picks the agent, builds the model input, and takes the turn.</summary>
    /// <param name="userInput">What the caller said.</param>
    /// <returns>Everything the rest of the turn needs.</returns>
    private Turn BeginTurn(string userInput)
    {
        if (IsComplete)
        {
            throw new InvalidOperationException(
                $"The call '{CallId}' reached the terminal stage '{Stage}', so it runs no further turn.");
        }

        var agent = ResolveAgent();

        // One session runs one turn at a time. The state document takes no lock, so a second turn
        // that overlapped the first would corrupt it rather than fail.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException(
                $"A turn of the call '{CallId}' is still running. One call runs one turn at a time.");
        }

        // The reminder rides a request that happens anyway, and it rides exactly one. The transcript
        // keeps what the caller said, so a stale reminder never repeats in a later turn.
        var reminder = _policy is null ? null : UnfilledSlotReminder.Build(State, _policy.CurrentStage);
        ChatMessage spoken = new(ChatRole.User, userInput);
        List<ChatMessage> request =
            [.. _transcript, new ChatMessage(ChatRole.User, UnfilledSlotReminder.Prepend(reminder, userInput))];

        _transcript.Add(spoken);
        return new Turn(agent, request, spoken, State.Stage, State.TurnIndex);
    }

    /// <summary>Runs every writer, then lets the machine pick the stage of the next turn.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="response">What the agent answered.</param>
    /// <param name="cancellationToken">Cancels the extractor call.</param>
    /// <returns>The finished turn.</returns>
    private async Task<TurnResult> CompleteTurnAsync(
        Turn turn,
        AgentResponse response,
        CancellationToken cancellationToken)
    {
        _transcript.AddRange(response.Messages);

        // Writer order, step 2.
        ApplyToolResults(response.Messages);

        // Writer order, step 3.
        var extractionFailure = await ExtractAsync(turn, response, cancellationToken).ConfigureAwait(false);

        // Writer order, step 4. The clock comes from the injected provider, so a test owns it.
        State.TurnIndex++;
        State.CallDurationSeconds = (_time.GetUtcNow() - _startedAt).TotalSeconds;

        // Writer order, step 5.
        _counters.Apply(State);

        var stageAfter = turn.StageBefore;
        if (_policy is not null)
        {
            stageAfter = _policy.Advance(State.Snapshot());
            State.Stage = stageAfter;
            IsComplete = _policy.IsTerminal;
        }

        return new TurnResult(
            CallId,
            turn.Index,
            turn.StageBefore,
            stageAfter,
            response.Text,
            IsComplete,
            extractionFailure);
    }

    /// <summary>Runs the extractor against the finished turn.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="response">What the agent answered.</param>
    /// <param name="cancellationToken">Cancels the model call.</param>
    /// <returns>The reason the extractor produced nothing, or <see langword="null"/>.</returns>
    private async Task<string?> ExtractAsync(Turn turn, AgentResponse response, CancellationToken cancellationToken)
    {
        if (_extractor is null || _compiled.Configuration.Extractor is not { When: ExtractorTrigger.AfterReply })
        {
            return null;
        }

        // The extractor reads one finished turn and not the whole call, which is what its own prompt
        // asks for. The state document already carries every earlier answer, so nothing is lost.
        // The caller's message goes in without the reminder, because a reminder is not a fact.
        List<ChatMessage> finished = [turn.Spoken, .. response.Messages];

        // A failed extraction never drops the turn. The result carries the reason instead.
        var result = await _extractor.ExtractAsync(State, finished, cancellationToken).ConfigureAwait(false);
        return result.Failure;
    }

    /// <summary>Fills every tool-written slot from the tool results of one turn.</summary>
    /// <param name="messages">The messages the agent produced, oldest first.</param>
    private void ApplyToolResults(IEnumerable<ChatMessage> messages)
    {
        // The result carries the call id, and the call carries the name. The name is the declared
        // tool id, because the compile table names every function after the tools: entry it built.
        Dictionary<string, string> toolIdByCall = new(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    toolIdByCall[call.CallId] = call.Name;
                    continue;
                }

                if (content is FunctionResultContent result
                    && toolIdByCall.TryGetValue(result.CallId, out var toolId))
                {
                    ToolStateWriter.Apply(State, toolId, ToNode(result.Result));
                }
            }
        }
    }

    /// <summary>Picks the agent that speaks this turn.</summary>
    /// <returns>The agent.</returns>
    private AIAgent ResolveAgent()
    {
        if (_policy is null)
        {
            // Row 1, row 3, and row 4 of the compile table. One entry agent answers every turn.
            return _compiled.Agent;
        }

        if (_policy.CurrentAgentId is not { Length: > 0 } || _compiled.ForStage(_policy.Stage) is not { } agent)
        {
            throw new InvalidOperationException(
                $"The stage '{_policy.Stage}' of the call '{CallId}' names no agent, so no turn can run.");
        }

        return agent;
    }

    /// <summary>Carries one tool result into the node tree the tool writer reads.</summary>
    /// <param name="value">Whatever the tool returned.</param>
    /// <returns>The node tree, or <see langword="null"/> when the tool returned nothing.</returns>
    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),

        // A tool result has no declared shape. A tool that answers with a JSON document as one
        // string still reaches its slot, and a tool that answers with prose reads as that prose.
        JsonElement element when element.ValueKind is JsonValueKind.String
            => ParseOrText(element.GetString() ?? string.Empty),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => ParseOrText(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>Reads one string as JSON, and falls back to the string itself.</summary>
    /// <param name="text">The text the tool returned.</param>
    /// <returns>The node tree.</returns>
    private static JsonNode? ParseOrText(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Section 8.7: a tool result has no declared shape, and a tool never drops a turn.
            return JsonValue.Create(text);
        }
    }

    /// <summary>Everything one turn carries from its start to its end.</summary>
    /// <param name="Agent">The agent the stage names.</param>
    /// <param name="Request">The messages the run reads, with the reminder on the last one.</param>
    /// <param name="Spoken">What the caller said, without the reminder.</param>
    /// <param name="StageBefore">The stage the turn spoke in.</param>
    /// <param name="Index">The zero-based index of the turn.</param>
    private sealed record Turn(
        AIAgent Agent,
        List<ChatMessage> Request,
        ChatMessage Spoken,
        string StageBefore,
        int Index);
}
