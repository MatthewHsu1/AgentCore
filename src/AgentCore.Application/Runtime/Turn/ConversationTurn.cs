using System.Diagnostics;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
    // The turn's working set, carried from preparation through commit. Built once per turn in
    // ConversationTurnRunner.BeginTurn for the same reason Activity is: ConversationTurnStream reopens the state
    // scope on every step of a streaming turn, and collectors built per step would lose whatever an
    // earlier step cited or published before a later step could attach it to a message.
    // Waiting holds the call ids of the approval requests an earlier turn already stored, read before the run
    // consumes MAF's queue, so a request shown again is not stored twice.
    // Before is what was said on a call ahead of Spoken that no turn answered (TurnCommit.Before).
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
        ConversationTurnOrigin? Origin,
        IReadOnlyCollection<string> Waiting,
        IReadOnlyList<ChatMessage> Before);
}
