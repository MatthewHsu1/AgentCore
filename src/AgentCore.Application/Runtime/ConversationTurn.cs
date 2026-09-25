using System.Diagnostics;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime
{
    // The turn's working set, carried from preparation through commit. Built once per turn in
    // ConversationTurnRunner.BeginTurn for the same reason Activity is: ConversationTurnStream reopens the state
    // scope on every step of a streaming turn, and collectors built per step would lose whatever an
    // earlier step cited or published before a later step could attach it to a message.
    internal sealed record ConversationTurn(
        AIAgent Agent,
        AgentSession Session,
        ChatMessage Spoken,
        string? Reminder,
        string StageBefore,
        int Index,
        Activity? Activity,
        long StartedAt,
        TurnSources Sources,
        TurnResults Results,
        TurnFiles Files,
        KnowledgeScope? Knowledge,
        ConversationTurnOrigin? Origin);
}
