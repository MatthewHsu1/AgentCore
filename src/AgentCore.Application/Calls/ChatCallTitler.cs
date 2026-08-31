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
    /// <remarks>
    /// Messages and not turns: one turn writes several when tools are called. A six-word title does
    /// not improve for having read the whole call, and the whole call is what the prompt would be
    /// charged for.
    /// </remarks>
    private const int MaxMessagesRead = 6;

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

        List<ChatMessage> prompt = [.. rows.Take(MaxMessagesRead).Select(row => row.Content)];

        prompt.Add(new ChatMessage(ChatRole.User, Instruction));

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
