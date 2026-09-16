using System.Diagnostics;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

// The turn's working set, carried from preparation through commit. Built once per turn in
// CallTurnRunner.BeginTurn for the same reason Activity is: CallTurnStream reopens the state
// scope on every step of a streaming turn, and collectors built per step would lose whatever an
// earlier step drew before a later step could attach it to a message.
internal sealed record CallTurn(
    AIAgent Agent,
    AgentSession Session,
    List<ChatMessage> Request,
    ChatMessage Spoken,
    string? Reminder,
    string StageBefore,
    int Index,
    Activity? Activity,
    long StartedAt,
    TurnRenders? Renders,
    TurnSources Sources,
    TurnResults Results,
    KnowledgeScope? Knowledge,
    string? MessageId);
