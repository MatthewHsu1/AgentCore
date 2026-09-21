using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// The cheap half of compaction: once the words bound for the model pass the trigger, every tool
    /// result older than the newest turns is cut to a fixed length. One message in, one message out,
    /// same id, so the row numbers the summary stage reads survive. Nothing is stored; the cut is
    /// re-made from the rows every turn, and it is the same cut every turn, so the prompt prefix the
    /// model's cache keys on does not move.
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal sealed class ToolResultCapProvider : AIContextProvider
    {
        private const char Cut = '…';

        private readonly CompactionTrigger _trigger;

        private readonly int _keepTurns;

        private readonly int _maxResultChars;

        /// <param name="trigger">When the cap applies, judged over everything bound for the model.</param>
        /// <param name="keepTurns">Turns. The newest turns whose results stay whole. A turn starts at a user message.</param>
        /// <param name="maxResultChars">Characters. What a capped result keeps.</param>
        public ToolResultCapProvider(CompactionTrigger trigger, int keepTurns, int maxResultChars)
        {
            ArgumentNullException.ThrowIfNull(trigger);
            ArgumentOutOfRangeException.ThrowIfNegative(keepTurns);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResultChars);

            _trigger = trigger;
            _keepTurns = keepTurns;
            _maxResultChars = maxResultChars;
        }

        /// <inheritdoc />
        protected override ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            AIContext input = context.AIContext;
            if (input.Messages is null)
            {
                return new(input);
            }

            IReadOnlyList<ChatMessage> messages = input.Messages as IReadOnlyList<ChatMessage> ?? [.. input.Messages];
            if (!_trigger(Measure(messages)))
            {
                return new(input);
            }

            List<ChatMessage> capped = new(messages.Count);
            int kept = KeptFrom(messages);
            for (int index = 0; index < messages.Count; index++)
            {
                capped.Add(index < kept ? Cap(messages[index]) : messages[index]);
            }

            return new(new AIContext
            {
                Instructions = input.Instructions,
                Messages = capped,
                Tools = input.Tools,
            });
        }

        /// <summary>
        /// One group holding every message is enough for the token triggers, and it makes this stage
        /// and the summary stage count the same way.
        /// </summary>
        private static CompactionMessageIndex Measure(IReadOnlyList<ChatMessage> messages)
        {
            CompactionMessageIndex index = new([]);
            _ = index.AddGroup(CompactionGroupKind.User, messages);
            return index;
        }

        /// <summary>The index of the first message of the newest kept turn, or the count when none is kept.</summary>
        private int KeptFrom(IReadOnlyList<ChatMessage> messages)
        {
            int turns = 0;
            for (int index = messages.Count - 1; index >= 0; index--)
            {
                if (messages[index].Role == ChatRole.User && ++turns == _keepTurns)
                {
                    return index;
                }
            }

            return _keepTurns == 0 ? messages.Count : 0;
        }

        private ChatMessage Cap(ChatMessage message)
        {
            return message.Role != ChatRole.Tool || !message.Contents.OfType<FunctionResultContent>().Any(Long)
                ? message
                : new ChatMessage(message.Role, [.. message.Contents.Select(content => content is FunctionResultContent result && Long(result) ? Cap(result) : content)])
                {
                    AuthorName = message.AuthorName,
                    MessageId = message.MessageId,
                    CreatedAt = message.CreatedAt,
                    AdditionalProperties = message.AdditionalProperties,
                };
        }

        private FunctionResultContent Cap(FunctionResultContent result)
        {
            return new(result.CallId, result.Result!.ToString()![.._maxResultChars] + Cut);
        }

        private bool Long(FunctionResultContent result)
        {
            return result.Result?.ToString() is { } text && text.Length > _maxResultChars;
        }
    }
#pragma warning restore MAAI001
}
