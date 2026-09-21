using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Conversation
{
    /// <summary>
    /// Refuses a document whose conversation transport and speech vendors cannot coexist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A transport whose frames already carry text <i>is</i> the recognizer and the synthesizer. A
    /// document naming it for the pipe and something else for the ears or the mouth describes a
    /// deployment that cannot exist, and the failure it would otherwise produce — a conversation that connects
    /// and never transcribes — is among the most expensive kinds to diagnose.
    /// </para>
    /// <para>
    /// The two roles are checked one at a time. A document that gets both wrong is wrong twice, and
    /// hearing about one of them, fixing it, and starting again to hear about the other is the slow way
    /// to learn that.
    /// </para>
    /// <para>
    /// <b>No vendor name appears here.</b> <see cref="IConversationAdapter.CarriesText"/> is declared by the
    /// adapter, so a second bundled vendor is one new adapter and no edit to this file. A guard that
    /// grew an <c>||</c> per vendor is the design this one exists to avoid.
    /// </para>
    /// <para>
    /// It runs from <c>AddAgentCoreAsync</c> rather than from the route extension, so a host that
    /// forgets to map a route still learns its document is self-contradictory.
    /// </para>
    /// </remarks>
    public static class ConversationSpeechPairing
    {
        /// <summary>Checks that both speech roles are compatible with the selected conversation transport.</summary>
        /// <param name="conversation">The <c>providers.conversation</c> block.</param>
        /// <param name="speech">The <c>providers.speech</c> block, with both of its roles.</param>
        /// <param name="selectedConversation">The adapter <c>providers.conversation.kind</c> selected.</param>
        /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
        /// <exception cref="ConfigurationLoadException">
        /// The transport carries text and at least one speech role names a different vendor. The
        /// exception carries one error per offending role, recognition first.
        /// </exception>
        public static void Validate(
            ConversationProviderConfiguration conversation,
            SpeechProviderConfiguration speech,
            IConversationAdapter selectedConversation)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            ArgumentNullException.ThrowIfNull(speech);
            ArgumentNullException.ThrowIfNull(selectedConversation);

            if (!selectedConversation.CarriesText)
            {
                // The socket carries audio, so a separate speech vendor is exactly what is wanted and
                // the kinds are expected to differ from the conversation's.
                return;
            }

            List<ConfigurationError> errors = [];

            Check(errors, conversation.Kind, speech.Stt.Kind, "stt");
            Check(errors, conversation.Kind, speech.Tts.Kind, "tts");

            if (errors.Count > 0)
            {
                throw new ConfigurationLoadException(errors);
            }
        }

        /// <summary>Adds one error when a role names a vendor the text-carrying transport is not.</summary>
        /// <param name="errors">The errors collected so far. One start reports every offending role.</param>
        /// <param name="conversationKind">The vendor <c>providers.conversation.kind</c> names.</param>
        /// <param name="roleKind">The vendor this role names.</param>
        /// <param name="role">The role's own name in the document, <c>stt</c> or <c>tts</c>.</param>
        private static void Check(List<ConfigurationError> errors, string conversationKind, string roleKind, string role)
        {
            if (string.Equals(roleKind, conversationKind, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            errors.Add(new ConfigurationError
            {
                Pointer = $"/providers/speech/{role}/kind",
                Message =
                    $"providers.conversation is kind: {conversationKind}, and that transport carries text, so it "
                    + $"performs recognition and synthesis itself. providers.speech.{role} is kind: "
                    + $"{roleKind}, which would never be asked to do anything. Set "
                    + $"providers.speech.{role} to '{conversationKind}', or choose a conversation transport that "
                    + $"carries audio.",
                Check = ConfigurationCheck.ReferenceResolution,
            });
        }
    }
}
