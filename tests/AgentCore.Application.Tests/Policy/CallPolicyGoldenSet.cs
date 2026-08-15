using System.Text.Json.Nodes;

namespace AgentCore.Application.Tests.Policy;

/// <summary>The stage of a service call. The stage set of <c>spike/callpolicy</c>.</summary>
internal enum CallStage
{
    Greeting,
    Identify,
    Classify,
    Resolve,
    Escalate,
    Close,
}

/// <summary>
/// Everything the turn loop learned about the call so far. The externalized, typed state.
/// </summary>
internal sealed record CallFacts
{
    public static readonly CallFacts Empty = new();

    public string? Model { get; init; }

    public string? Serial { get; init; }

    public string? FaultCode { get; init; }

    public bool ProblemDescribed { get; init; }

    public bool Resolved { get; init; }

    public bool CallerAskedForHuman { get; init; }

    public bool CallerSaidGoodbye { get; init; }

    public int FailedResolveTurns { get; init; }

    public bool MachineIdentified => Model is not null && Serial is not null;

    public bool ProblemKnown => FaultCode is not null || ProblemDescribed;

    /// <summary>Writes the facts as the state snapshot a configuration-declared guard reads.</summary>
    public IReadOnlyDictionary<string, JsonNode?> ToSnapshot(string stage, int turnIndex)
        => new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["model"] = Model is null ? null : JsonValue.Create(Model),
            ["serial"] = Serial is null ? null : JsonValue.Create(Serial),
            ["faultCode"] = FaultCode is null ? null : JsonValue.Create(FaultCode),
            ["problemDescribed"] = JsonValue.Create(ProblemDescribed),
            ["resolved"] = JsonValue.Create(Resolved),
            ["callerAskedForHuman"] = JsonValue.Create(CallerAskedForHuman),
            ["callerSaidGoodbye"] = JsonValue.Create(CallerSaidGoodbye),
            ["failedResolveTurns"] = JsonValue.Create(FailedResolveTurns),
            ["stage"] = JsonValue.Create(stage),
            ["turnIndex"] = JsonValue.Create(turnIndex),
            ["callDurationSeconds"] = JsonValue.Create(0d),
        };
}

/// <summary>
/// The hand-written transition table of <c>spike/callpolicy</c>, copied unchanged.
/// </summary>
/// <remarks>
/// Rule 15 of section 11: the six golden calls replay against a configuration-declared policy and
/// reproduce this machine exactly. Nothing else in the test holds a transition rule, so the
/// comparison stays about the runtime and not about the rules.
/// </remarks>
internal static class Transitions
{
    public const int EscalateAfterTurns = 3;

    public static CallStage Next(CallStage current, CallFacts f) => current switch
    {
        CallStage.Greeting => CallStage.Identify,

        CallStage.Identify when f.CallerSaidGoodbye => CallStage.Close,
        CallStage.Identify when f.CallerAskedForHuman => CallStage.Escalate,
        CallStage.Identify when f.MachineIdentified => CallStage.Classify,
        CallStage.Identify => CallStage.Identify,

        CallStage.Classify when f.CallerSaidGoodbye => CallStage.Close,
        CallStage.Classify when f.CallerAskedForHuman => CallStage.Escalate,
        CallStage.Classify when f.ProblemKnown => CallStage.Resolve,
        CallStage.Classify => CallStage.Classify,

        CallStage.Resolve when f.CallerSaidGoodbye => CallStage.Close,
        CallStage.Resolve when f.CallerAskedForHuman => CallStage.Escalate,
        CallStage.Resolve when f.Resolved => CallStage.Close,
        CallStage.Resolve when f.FailedResolveTurns >= EscalateAfterTurns => CallStage.Escalate,
        CallStage.Resolve => CallStage.Resolve,

        CallStage.Escalate => CallStage.Close,
        CallStage.Close => CallStage.Close,

        _ => throw new ArgumentOutOfRangeException(nameof(current), current, null),
    };

    /// <summary>Maps one stage to the id the configuration declares.</summary>
    public static string ToStageId(CallStage stage) => stage switch
    {
        CallStage.Greeting => "greeting",
        CallStage.Identify => "identify",
        CallStage.Classify => "classify",
        CallStage.Resolve => "resolve",
        CallStage.Escalate => "escalate",
        _ => "close",
    };
}

/// <summary>One caller turn: the facts the turn loop observed after it ended.</summary>
internal sealed record GoldenTurn(string Label, CallFacts Facts);

/// <summary>One whole call, replayed against a policy.</summary>
internal sealed record GoldenCall(string Name, IReadOnlyList<GoldenTurn> Turns);

/// <summary>
/// The six deterministic call scenarios of <c>spike/callpolicy</c>. No model runs.
/// </summary>
internal static class GoldenSet
{
    public static IReadOnlyList<GoldenCall> Calls { get; } =
    [
        new("happy-path-fault-code",
        [
            new("caller says hello", CallFacts.Empty),
            new("gives model and serial", new CallFacts { Model = "F85", Serial = "SF240117" }),
            new("reads the fault code", new CallFacts { Model = "F85", Serial = "SF240117", FaultCode = "E7" }),
            new("follows the fix", new CallFacts { Model = "F85", Serial = "SF240117", FaultCode = "E7", Resolved = true }),
        ]),

        new("slow-identification",
        [
            new("caller says hello", CallFacts.Empty),
            new("gives model only", new CallFacts { Model = "F80" }),
            new("cannot find the serial", new CallFacts { Model = "F80" }),
            new("finds the serial", new CallFacts { Model = "F80", Serial = "SF230902" }),
            new("describes a noise", new CallFacts { Model = "F80", Serial = "SF230902", ProblemDescribed = true }),
            new("fix works", new CallFacts { Model = "F80", Serial = "SF230902", ProblemDescribed = true, Resolved = true }),
        ]),

        new("escalate-after-three-failures",
        [
            new("caller says hello", CallFacts.Empty),
            new("gives model and serial", new CallFacts { Model = "TT8", Serial = "SF251103" }),
            new("describes the problem", new CallFacts { Model = "TT8", Serial = "SF251103", ProblemDescribed = true }),
            new("fix 1 fails", new CallFacts { Model = "TT8", Serial = "SF251103", ProblemDescribed = true, FailedResolveTurns = 1 }),
            new("fix 2 fails", new CallFacts { Model = "TT8", Serial = "SF251103", ProblemDescribed = true, FailedResolveTurns = 2 }),
            new("fix 3 fails", new CallFacts { Model = "TT8", Serial = "SF251103", ProblemDescribed = true, FailedResolveTurns = 3 }),
            new("transfer completes", new CallFacts { Model = "TT8", Serial = "SF251103", ProblemDescribed = true, FailedResolveTurns = 3 }),
        ]),

        new("caller-demands-a-human-immediately",
        [
            new("caller says hello", CallFacts.Empty),
            new("asks for a person", new CallFacts { CallerAskedForHuman = true }),
            new("transfer completes", new CallFacts { CallerAskedForHuman = true }),
        ]),

        new("caller-hangs-up-mid-diagnosis",
        [
            new("caller says hello", CallFacts.Empty),
            new("gives model and serial", new CallFacts { Model = "F63", Serial = "SF221201" }),
            new("says goodbye", new CallFacts { Model = "F63", Serial = "SF221201", CallerSaidGoodbye = true }),
        ]),

        new("human-request-beats-a-resolved-fix",
        [
            new("caller says hello", CallFacts.Empty),
            new("gives model and serial", new CallFacts { Model = "E95", Serial = "SF260401" }),
            new("gives the fault code", new CallFacts { Model = "E95", Serial = "SF260401", FaultCode = "E1" }),

            // Both Resolved and CallerAskedForHuman are true. Precedence must be deterministic.
            new("fixed but still wants a person",
                new CallFacts { Model = "E95", Serial = "SF260401", FaultCode = "E1", Resolved = true, CallerAskedForHuman = true }),
        ]),
    ];

    /// <summary>Replays one call against the hand-written table.</summary>
    public static IReadOnlyList<CallStage> ReplayHandWritten(GoldenCall call)
    {
        var stage = CallStage.Greeting;
        List<CallStage> stages = new(call.Turns.Count);
        foreach (var turn in call.Turns)
        {
            stage = Transitions.Next(stage, turn.Facts);
            stages.Add(stage);
        }

        return stages;
    }

    /// <summary>
    /// The same six stages, declared in configuration. This is the document rule 15 replays against.
    /// </summary>
    public const string Yaml =
        """
        apiVersion: agentcore/v1
        name: call-policy-golden

        state:
          model:               { type: string,  writer: extractor, description: the machine model }
          serial:              { type: string,  writer: extractor, description: the serial number }
          faultCode:           { type: string,  writer: extractor, description: the fault code on the console }
          problemDescribed:    { type: boolean, default: false, writer: extractor }
          resolved:            { type: boolean, default: false, writer: extractor }
          callerAskedForHuman: { type: boolean, default: false, writer: extractor }
          callerSaidGoodbye:   { type: boolean, default: false, writer: extractor }
          failedResolveTurns:
            type: integer
            default: 0
            writer: counter
            increment:
              and:
                - { "===": [ { var: stage }, "resolve" ] }
                - { "!": { var: resolved } }

        extractor:
          model: { ref: fill }
          when: after_reply

        guards:
          saidGoodbye:
            { var: callerSaidGoodbye }

          wantsHuman:
            and:
              - { "!": { var: callerSaidGoodbye } }
              - { var: callerAskedForHuman }

          machineIdentified:
            and:
              - { "!": { var: callerSaidGoodbye } }
              - { "!": { var: callerAskedForHuman } }
              - { "!!": [ { var: model } ] }
              - { "!!": [ { var: serial } ] }

          problemKnown:
            and:
              - { "!": { var: callerSaidGoodbye } }
              - { "!": { var: callerAskedForHuman } }
              - or:
                  - { "!!": [ { var: faultCode } ] }
                  - { var: problemDescribed }

          goodbyeOrFixed:
            or:
              - { var: callerSaidGoodbye }
              - and:
                  - { "!": { var: callerAskedForHuman } }
                  - { var: resolved }

          humanOrExhausted:
            and:
              - { "!": { var: callerSaidGoodbye } }
              - or:
                  - { var: callerAskedForHuman }
                  - and:
                      - { "!": { var: resolved } }
                      - { ">=": [ { var: failedResolveTurns }, 3 ] }

        agents:
          defaults:
            model: { ref: reply, temperature: 0.3 }
            instructions: |
              <the stable cached prefix>
          items:
            - { id: greeter,    instructions: "<greeting delta>" }
            - { id: identifier, instructions: "<identify delta>" }
            - { id: classifier, instructions: "<classify delta>" }
            - { id: resolver,   instructions: "<resolve delta>" }
            - { id: escalator,  instructions: "<escalate delta>" }
            - { id: closer,     instructions: "<close delta>" }

        policy:
          initial: greeting
          stages:
            - id: greeting
              agent: greeter
              to: [ { stage: identify } ]

            - id: identify
              agent: identifier
              to:
                - { stage: close,    when: saidGoodbye }
                - { stage: escalate, when: wantsHuman }
                - { stage: classify, when: machineIdentified }

            - id: classify
              agent: classifier
              to:
                - { stage: close,    when: saidGoodbye }
                - { stage: escalate, when: wantsHuman }
                - { stage: resolve,  when: problemKnown }

            - id: resolve
              agent: resolver
              to:
                - { stage: close,    when: goodbyeOrFixed }
                - { stage: escalate, when: humanOrExhausted }

            - id: escalate
              agent: escalator
              to: [ { stage: close } ]

            - id: close
              agent: closer
              terminal: true

        providers:
          call:   { kind: telnyx-relay }
          speech: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
            - { kind: openai, model: gpt-5.4-nano, as: fill }
        """;
}
