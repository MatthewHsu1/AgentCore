using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.TestSupport;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>A store kept in this process, with the conversations made against its words recorded.</summary>
internal sealed class RecordingConversationStore() : DelegatingConversationStore(new InMemoryConversationStore())
{
    private readonly Lock _lock = new();

    /// <summary>The words as the store holds them now. It backs <see cref="Live"/>, not <see cref="Rows"/>.</summary>
    private readonly Dictionary<(string ConversationId, int Ordinal), ConversationMessage> _rows = [];

    /// <summary>Gets every row the provider appended, in the order it appended them.</summary>
    public List<ConversationMessage> Rows { get; } = [];

    /// <summary>Gets every rewrite the provider asked for, in order.</summary>
    public List<ConversationMessage> Rewrites { get; } = [];

    /// <summary>Gets how many times a whole conversation was read back.</summary>
    public int Reads { get; private set; }

    /// <summary>Reads one conversation as the store holds it now, oldest message first.</summary>
    /// <param name="conversationId">The conversation to read.</param>
    public IReadOnlyList<ConversationMessage> Live(string conversationId)
    {
        lock (_lock)
        {
            return [.. _rows.Values.Where(row => row.ConversationId == conversationId).OrderBy(row => row.Ordinal)];
        }
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
        string conversationId,
        IReadOnlyList<ConversationMessageDraft> messages,
        ConversationSessionState? state = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await base.AppendAsync(conversationId, messages, state, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            Rows.AddRange(rows);
            foreach (var row in rows)
            {
                _rows[(row.ConversationId, row.Ordinal)] = row;
            }
        }

        return rows;
    }

    /// <inheritdoc />
    public override async ValueTask RewriteAsync(
        string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
    {
        await base.RewriteAsync(conversationId, messageId, content, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            foreach (var pair in _rows)
            {
                if (pair.Key.ConversationId != conversationId || pair.Value.MessageId != messageId)
                {
                    continue;
                }

                var rewritten = pair.Value with { Content = content };
                _rows[pair.Key] = rewritten;
                Rewrites.Add(rewritten);
                break;
            }
        }
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<ConversationMessage>> ReadAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Reads++;
        }

        return ValueTask.FromResult(Live(conversationId));
    }

    /// <inheritdoc />
    public override ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var going = _rows.Keys.Where(key => key.ConversationId == conversationId).ToList();
            foreach (var key in going)
            {
                _rows.Remove(key);
            }

            return ValueTask.FromResult(going.Count);
        }
    }
}
