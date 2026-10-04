using System.Collections.Concurrent;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Transcript;

namespace AgentCore.Application.Hooks.BuiltIn
{
    /// <summary>
    /// Adds an accepted call's brief to every run of the call's conversation, as instructions.
    /// AgentCore never reads, wraps, or changes the brief; the host writes all of it.
    /// </summary>
    internal sealed class CallBriefHook : AgentHook
    {
        /// <summary>
        /// The one line a call whose vendor speaks for itself adds to every run, so the agent reads the
        /// <see cref="FrontVoice"/> lines in its history as already heard.
        /// </summary>
        internal const string FrontVoiceNote =
            $"Messages from {FrontVoice.AuthorName} are what the phone's front voice already said to the caller: answer only the caller's latest message, and correct a {FrontVoice.AuthorName} line only if it was wrong.";

        private readonly ConcurrentDictionary<string, (string? Brief, bool FrontVoice)> _briefs = new(StringComparer.Ordinal);

        /// <summary>Files the brief of the call that runs on <paramref name="conversationId"/>.</summary>
        /// <param name="conversationId">The conversation the call runs on.</param>
        /// <param name="brief">The brief, added to every run verbatim, or <see langword="null"/> for none.</param>
        /// <param name="frontVoice">Whether the call's vendor speaks for itself, so every run also gets <see cref="FrontVoiceNote"/>.</param>
        internal void Set(string conversationId, string? brief, bool frontVoice = false)
        {
            ArgumentException.ThrowIfNullOrEmpty(conversationId);
            _briefs[conversationId] = (brief, frontVoice);
        }

        /// <summary>Forgets the brief: the call ended.</summary>
        /// <param name="conversationId">The conversation the call ran on.</param>
        internal void Clear(string conversationId)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            _ = _briefs.TryRemove(conversationId, out _);
        }

        /// <inheritdoc />
        public override ValueTask BeforeRunAsync(RunGate gate, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(gate);

            if (gate.Scope.ConversationId is { } conversationId && _briefs.TryGetValue(conversationId, out (string? Brief, bool FrontVoice) call))
            {
                if (call.Brief is { } brief)
                {
                    gate.AddInstructions(brief);
                }

                if (call.FrontVoice)
                {
                    gate.AddInstructions(FrontVoiceNote);
                }
            }

            return default;
        }
    }
}
