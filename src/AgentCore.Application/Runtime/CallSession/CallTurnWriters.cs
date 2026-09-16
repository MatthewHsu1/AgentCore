using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.State;
using AgentCore.Application.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

internal sealed class CallTurnWriters
{
    private readonly CallSession _session;

    internal CallTurnWriters(CallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Runs the extractor against the finished turn.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="response">What the agent answered.</param>
    /// <param name="cancellationToken">Cancels the model call.</param>
    /// <returns>The reason the extractor produced nothing, or <see langword="null"/>.</returns>
    internal async Task<string?> ExtractAsync(CallTurn turn, AgentResponse response, CancellationToken cancellationToken)
    {
        if (_session.Extractor is null || _session.Compiled.Configuration.Extractor is not { When: ExtractorTrigger.AfterReply })
        {
            return null;
        }

        // The extractor reads the finished turn and, in front of it, the last thing the agent said
        // before the caller spoke. A caller answering a question — "the ENT one", "yes", "the second
        // one" — cannot be read without the question, and the state document cannot carry it because
        // nothing was written yet. The rest of the call stays out: the document already carries every
        // earlier answer. Measured 2026-09-02: with the question in view every such answer resolved,
        // and a turn where only the agent had named a machine still filled nothing.
        List<ChatMessage> finished = [turn.Spoken, .. response.Messages];
        if (_session.Transcript.LastOrDefault(message => message.Role == ChatRole.Assistant && message.Text.Length > 0) is { } asked)
        {
            finished.Insert(0, asked);
        }

        // A failed extraction never drops the turn. The result carries the reason instead.
        var result = await _session.Extractor.ExtractAsync(_session.State, finished, _session.Clarifications, cancellationToken)
            .ConfigureAwait(false);
            
        return result.Failure;
    }

    /// <summary>Fills every tool-written slot from the tool results of one turn.</summary>
    /// <param name="messages">The messages the agent produced, oldest first.</param>
    internal void ApplyToolResults(IEnumerable<ChatMessage> messages)
    {
        // The name a call carries is the declared tool id, because the compile table names every
        // function after the tools: entry it built.
        ToolCallNames names = new();

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        names.Called(call);
                        break;

                    case FunctionResultContent result when names.Of(result) is { } toolId:
                        ToolStateWriter.Apply(_session.State, toolId, ToolResultJson.ToNode(result.Result));
                        break;

                    default:
                        break;
                }
            }
        }
    }
}
