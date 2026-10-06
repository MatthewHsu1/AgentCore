namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A transport offers a call. A hook accepts it onto a conversation or rejects it.</summary>
    public sealed class CallGate : HookGate
    {
        internal const int MaxBriefLength = 1000;

        internal CallGate(
            HookScope scope,
            string entry,
            string callId,
            string? from,
            string? to,
            IReadOnlyDictionary<string, string> headers,
            string transport)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(callId);
            ArgumentNullException.ThrowIfNull(headers);
            ArgumentNullException.ThrowIfNull(transport);

            Entry = entry;
            CallId = callId;
            From = from;
            To = to;
            Headers = headers;
            Transport = transport;
        }

        /// <summary>Gets the entry the call's route serves.</summary>
        public string Entry { get; }

        /// <summary>Gets the transport's id for this call.</summary>
        public string CallId { get; }

        /// <summary>Gets the caller's number or address, when the transport knows it.</summary>
        public string? From { get; }

        /// <summary>Gets the number or address that was called, when the transport knows it.</summary>
        public string? To { get; }

        /// <summary>Gets the transport's headers, for example SIP headers.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>Gets the kind of transport that offered the call.</summary>
        public string Transport { get; }

        internal string? AcceptedConversationId { get; private set; }

        internal string? Brief { get; private set; }

        internal CallRefusal? Refusal { get; private set; }

        /// <summary>Takes the call onto a conversation. Terminal.</summary>
        /// <param name="conversationId">The conversation the call joins. AgentCore opens it or resumes it.</param>
        /// <param name="brief">Text the agent is told about this call. Over 1,000 characters it is cut to its first 1,000.</param>
        public void Accept(string conversationId, string? brief = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(conversationId);

            string? kept = brief is { Length: > MaxBriefLength } ? brief[..MaxBriefLength] : brief;

            Stage(terminal: true, () =>
            {
                AcceptedConversationId = conversationId;
                Brief = kept;
            });
        }

        /// <summary>Turns the call away. Each transport maps <paramref name="reason"/> to its own code. Terminal.</summary>
        /// <param name="reason">Why the call is refused.</param>
        public void Reject(CallRefusal reason)
        {
            Stage(terminal: true, () => Refusal = reason);
        }
    }
}
