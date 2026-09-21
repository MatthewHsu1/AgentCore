namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// The <see cref="Microsoft.Agents.AI.AgentSession.StateBag"/> key under which a background child's
    /// session carries the id of the conversation that started it.
    /// </summary>
    /// <remarks>
    /// A parent run has a turn filed in <c>TurnRegistry</c>, so its providers read the conversation id there.
    /// A child session has no turn: the framework starts it with fresh, empty state and null run
    /// options. <see cref="BackgroundChildAgent"/> stamps this key when the session is created, so the
    /// <c>file.publish</c> tool a background child calls can still find the conversation that owns the file
    /// when the turn lookup finds nothing.
    /// </remarks>
    internal static class BlobOwnerKey
    {
        /// <summary>The key. The value is the conversation id, a string.</summary>
        public const string Value = "urn:agentcore:blob-owner";
    }
}
