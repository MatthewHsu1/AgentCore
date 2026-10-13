using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// Removes reader content (<see cref="ReaderContent"/>) from the messages bound for the model. A file the caller
    /// attached is read back from the blob store first, so the model gets its bytes on every turn (<see cref="InboundFiles"/>).
    /// </summary>
    internal sealed class ReaderContentFilterProvider(IBlobStore? blobs = null) : AIContextProvider
    {
        /// <inheritdoc />
        protected override async ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            AIContext input = context.AIContext;
            if (input.Messages is null)
            {
                return input;
            }

            IEnumerable<ChatMessage> messages = input.Messages;
            if (blobs is not null
                && TurnRegistry.For(context.Session)?.ConversationId is { } owner
                && messages.Any(message => message.Contents.Any(content => content is FileContent file && InboundFiles.Is(file))))
            {
                messages = await InboundFiles.RehydrateAsync(blobs, owner, messages, cancellationToken).ConfigureAwait(false);
            }

            return new AIContext
            {
                Instructions = input.Instructions,
                Messages = ReaderContent.Strip(messages).ToList(),
                Tools = input.Tools,
            };
        }
    }
}
