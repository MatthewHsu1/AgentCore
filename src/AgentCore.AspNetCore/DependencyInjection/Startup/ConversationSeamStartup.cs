using AgentCore.Application.Conversation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using AgentCore.AspNetCore.Conversation;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>What the conversation seam produced while the host started.</summary>
    /// <param name="Conversation">The transports <see cref="AgentCoreOptions.UseConversation"/> registered.</param>
    /// <param name="Speech">
    /// The vendors <see cref="AgentCoreOptions.UseSpeech"/> registered, selected by nothing.
    /// <c>providers.speech</c> names the recognition vendor and the synthesis vendor, but no code in
    /// this solution turns either name into a constructed adapter: the one transport shipped today
    /// carries text, so it is itself both and there is nothing to build. The list goes in the container
    /// beside the document so that a vendor which does need constructing has somewhere to be found.
    /// </param>
    /// <param name="Handler">
    /// What the <c>MapCall</c> route runs for every entry, or <see langword="null"/> when this
    /// document routes no inbound conversation — the host registered no transport, wrote no
    /// <c>providers.conversation</c> block, or named a vendor this process dials out to, which has no inbound URL.
    /// </param>
    /// <param name="Unroutable">
    /// Why <paramref name="Handler"/> is <see langword="null"/>, in the words a deployer can act on, or
    /// <see langword="null"/> when conversations route.
    /// </param>
    internal readonly record struct ConversationSeamAdapters(
        IReadOnlyList<IConversationAdapter>? Conversation,
        IReadOnlyList<ISpeechAdapter>? Speech,
        RequestDelegate? Handler,
        string? Unroutable);

    /// <summary>The two provider blocks a conversation arrives on: <c>providers.conversation</c> and <c>providers.speech</c>.</summary>
    internal static class ConversationSeamStartup
    {
        /// <summary>Checks the two blocks agree, and hands back the vendor lists and the handler to register.</summary>
        /// <param name="configuration">The loaded document. It carries both provider blocks.</param>
        /// <param name="options">The options the host filled. It carries the registered vendors.</param>
        /// <returns>The two lists and the handler, each one or <see langword="null"/> when the host registered none.</returns>
        internal static ConversationSeamAdapters Build(
            AgentCoreConfiguration configuration,
            AgentCoreOptions options)
        {
            if (options.ConversationAdapters is not { } conversationAdapters)
            {
                return new ConversationSeamAdapters(
                    null, options.Speech, null, "this host registered no conversation adapter");
            }

            // Registering a vendor is what turns this seam on. A host that registered none is not asked
            // what its document says, exactly as telemetry, knowledge, and moderation are not read when
            // a host registered no adapter for them.
            ConversationProviderConfiguration conversationEntry = configuration.Providers?.Conversation
                ?? throw MissingConversationBlock();

            IConversationAdapter selectedConversation = VendorAdapterSelector.Select(
                conversationEntry.Kind, conversationAdapters, ConversationSeams.Conversation);

            SpeechProviderConfiguration speechEntry = configuration.Providers?.Speech
                ?? throw MissingSpeechBlock();

            // Read here, and not only where the route is mapped. Agreement between two document entries
            // is a document fact: it is true or false whether or not anything is routed, so a host that
            // forgets app.MapCall() still learns its document contradicts itself.
            ConversationSpeechPairing.Validate(conversationEntry, speechEntry, selectedConversation);

            // Built here and not where the route is mapped, so an unusable limit in providers.conversation stops
            // the host rather than the first conversation that arrives on it. A vendor this process dials out to
            // has no inbound URL, and that is not a failure: it is the other half of the seam working.
            return selectedConversation is not IConversationTransportAdapter transport
                ? new ConversationSeamAdapters(
                    conversationAdapters,
                    options.Speech,
                    null,
                    $"'{selectedConversation.Kind}' is a vendor this process dials out to, so it answers no inbound route")
                : new ConversationSeamAdapters(conversationAdapters, options.Speech, transport.CreateHandler(conversationEntry), null);
        }

        /// <summary>Refuses a configuration that turned the conversation seam on and named no transport.</summary>
        /// <returns>The failure to throw, pointed at the block that is missing.</returns>
        private static ConfigurationLoadException MissingConversationBlock()
        {
            return new(new ConfigurationError
            {
                Pointer = "/providers/conversation",
                Message =
                            "This host registered a conversation transport with options.UseConversation(...), and this "
                            + "configuration writes no providers.conversation block for a kind to be picked from. A "
                            + "document that writes a providers section names both conversation and speech, because the "
                            + "schema requires them there; a document that writes no providers section at all is "
                            + "valid, and so is a configuration a host built in code, which passes through no "
                            + "schema. Write providers.conversation: { kind: ... }, or drop the options.UseConversation(...) call.",
                Check = ConfigurationCheck.ReferenceResolution,
            });
        }

        /// <summary>Refuses a configuration whose conversation transport has no speech block to be paired with.</summary>
        /// <returns>The failure to throw, pointed at the block that is missing.</returns>
        private static ConfigurationLoadException MissingSpeechBlock()
        {
            return new(new ConfigurationError
            {
                Pointer = "/providers/speech",
                Message =
                            "This configuration names providers.conversation and no providers.speech, so there is "
                            + "nothing to check the transport against: a transport that carries text is itself "
                            + "the recognizer, and one that carries audio needs a speech vendor named. A "
                            + "document that writes a providers section names both, because the schema requires "
                            + "them there; a document that writes no providers section at all is valid, and so "
                            + "is a configuration a host built in code, which passes through no schema. Write "
                            + "providers.speech with both of its roles: stt: { kind: ... } and tts: { kind: ... }.",
                Check = ConfigurationCheck.ReferenceResolution,
            });
        }
    }
}
