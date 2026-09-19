using System.Runtime.CompilerServices;
using System.Text;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Conversation;

/// <summary>The titler that asks the configured chat client.</summary>
/// <param name="conversations">The store, where the words are read and the finished title goes.</param>
/// <param name="client">The model that writes it.</param>
public sealed class ChatConversationTitler(IConversationStore conversations, IChatClient client) : IConversationTitler
{
    // Messages and not turns: one turn writes several when tools are called. A six-word title does
    // not improve for having read the whole conversation, and the whole conversation is what the prompt would be
    // charged for. The cap holds for a caller's messages too, which arrive unbounded.
    private const int MaxMessages = 6;

    // A system message
    private const string Instruction =
        "You name conversations. The user message holds a transcript, one line per turn. "
        + "Write a title for it: the topic the person raised, six words at most, "
        + "no quotation marks, no final stop. When the transcript raises no topic yet, "
        + "use the person's own first words as the title. Reply with the title alone.";

    /// <inheritdoc />
    public IAsyncEnumerable<string> GenerateAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        return GenerateCoreAsync(conversationId, cancellationToken);
    }

    /// <summary>Reads the conversation's messages and streams the title <see cref="NameAsync"/> writes.</summary>
    private async IAsyncEnumerable<string> GenerateCoreAsync(
        string conversationId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rows = await conversations.ReadAsync(conversationId, cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            yield break;
        }

        await foreach (var piece in NameAsync(conversationId, rows.Select(row => row.Content), cancellationToken)
            .ConfigureAwait(false))
        {
            yield return piece;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> GenerateFromAsync(
        string conversationId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messages);

        return GenerateFromCoreAsync(conversationId, messages, cancellationToken);
    }

    /// <summary>Streams the title of caller-supplied messages once the conversation proves to exist.</summary>
    private async IAsyncEnumerable<string> GenerateFromCoreAsync(
        string conversationId,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            yield break;
        }

        // Nothing else on this path touches the conversation, and RenameAsync on a missing one is a silent
        // no-op in both stores. Without this the model runs and its answer is dropped.
        if (await conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false) is null)
        {
            yield break;
        }

        await foreach (var piece in NameAsync(conversationId, messages, cancellationToken).ConfigureAwait(false))
        {
            yield return piece;
        }
    }

    /// <summary>Asks the model for a name, streams it, and writes the finished one to the conversation.</summary>
    /// <param name="conversationId">The conversation the finished title belongs to.</param>
    /// <param name="messages">The messages to name. The instruction is added here, not by the caller.</param>
    /// <param name="cancellationToken">Stops the generation, leaving the title as it was.</param>
    /// <returns>The title in pieces, in order.</returns>
    private async IAsyncEnumerable<string> NameAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<ChatMessage> prompt =
        [
            new ChatMessage(ChatRole.System, Instruction),
            new ChatMessage(ChatRole.User, Transcript(messages.Take(MaxMessages))),
        ];

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
            await conversations.RenameAsync(conversationId, whole, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Folds the messages into one text, one <c>role: words</c> line per message.</summary>
    private static string Transcript(IEnumerable<ChatMessage> messages)
    {
        StringBuilder text = new();

        foreach (var message in messages)
        {
            text.Append(message.Role.Value).Append(": ").AppendLine(message.Text);
        }

        return text.ToString();
    }
}
