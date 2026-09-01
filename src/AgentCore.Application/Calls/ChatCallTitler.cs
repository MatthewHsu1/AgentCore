using System.Runtime.CompilerServices;
using System.Text;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Calls;

/// <summary>The titler that asks the configured chat client.</summary>
/// <param name="calls">The store, where the words are read and the finished title goes.</param>
/// <param name="client">The model that writes it.</param>
public sealed class ChatCallTitler(ICallStore calls, IChatClient client) : ICallTitler
{
    // Messages and not turns: one turn writes several when tools are called. A six-word title does
    // not improve for having read the whole call, and the whole call is what the prompt would be
    // charged for. The cap holds for a caller's messages too, which arrive unbounded.
    private const int MaxMessages = 6;

    private const string Instruction =
        "Write a title for this conversation. Six words at most. No quotation marks, no final stop. "
        + "Reply with the title alone.";

    /// <inheritdoc />
    public async IAsyncEnumerable<string> GenerateAsync(
        string callId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        var rows = await calls.ReadAsync(callId, cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            yield break;
        }

        await foreach (var piece in NameAsync(callId, rows.Select(row => row.Content), cancellationToken)
            .ConfigureAwait(false))
        {
            yield return piece;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> GenerateFromAsync(
        string callId,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            yield break;
        }

        // Nothing else on this path touches the call, and RenameAsync on a missing one is a silent
        // no-op in both stores. Without this the model runs and its answer is dropped.
        if (await calls.GetAsync(callId, cancellationToken).ConfigureAwait(false) is null)
        {
            yield break;
        }

        await foreach (var piece in NameAsync(callId, messages, cancellationToken).ConfigureAwait(false))
        {
            yield return piece;
        }
    }

    /// <summary>Asks the model for a name, streams it, and writes the finished one to the call.</summary>
    /// <param name="callId">The call the finished title belongs to.</param>
    /// <param name="messages">The messages to name. The instruction is added here, not by the caller.</param>
    /// <param name="cancellationToken">Stops the generation, leaving the title as it was.</param>
    /// <returns>The title in pieces, in order.</returns>
    private async IAsyncEnumerable<string> NameAsync(
        string callId,
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<ChatMessage> prompt =
            [.. messages.Take(MaxMessages), new ChatMessage(ChatRole.User, Instruction)];

        StringBuilder title = new();

        await foreach (var update in client
            .GetStreamingResponseAsync(prompt, options: null, cancellationToken)
            .ConfigureAwait(false))
        {
            var piece = update.Text;

            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            title.Append(piece);
            yield return piece;
        }

        var whole = title.ToString().Trim();

        if (whole.Length > 0)
        {
            await calls.RenameAsync(callId, whole, cancellationToken).ConfigureAwait(false);
        }
    }
}
