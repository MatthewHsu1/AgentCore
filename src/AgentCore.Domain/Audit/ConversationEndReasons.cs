namespace AgentCore.Domain.Audit;

/// <summary>
/// The wire token of each <see cref="ConversationEndReason"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>conversation.ended</c> payload holds this token under <see cref="AuditPayloadKeys.EndReason"/>, and
/// never the .NET member name, and never the numeric value of the enum. This is the treatment
/// <see cref="AuditEventKinds"/> already gives an event kind, and it is the same argument: a rename
/// of a C# member must not change a hash PostgreSQL already stored, and the <c>CHECK</c> constraint
/// of D23 recomputes the same SHA-256 inside the engine, where no enum exists.
/// </para>
/// <para>
/// A token is stable forever. Add a token beside the old one, and never edit one in place. The
/// report that counts the endings of one year runs over these tokens, so a token that changes breaks
/// every count that came before it.
/// </para>
/// <para>
/// A token reads <c>&lt;who ended it&gt;.&lt;what they did&gt;</c>. <c>caller.hangup</c> takes the
/// vendor's own word, because the Conversation Control webhook that reports it is named <c>conversation.hangup</c>
/// and the adapter maps one to the other.
/// </para>
/// </remarks>
public static class ConversationEndReasons
{
    /// <summary>Reads the wire token of one reason.</summary>
    /// <param name="reason">The reason to name.</param>
    /// <returns>The token the <c>conversation.ended</c> payload writes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a member of the closed set.</exception>
    public static string ToToken(ConversationEndReason reason) => reason switch
    {
        ConversationEndReason.CallerHungUp => "caller.hangup",
        ConversationEndReason.AgentCompleted => "agent.completed",
        ConversationEndReason.TransferredToHuman => "agent.transferred",
        ConversationEndReason.Faulted => "conversation.faulted",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "The end-reason vocabulary is closed, and this value is not in it."),
    };

    /// <summary>Reads the reason behind one wire token.</summary>
    /// <param name="token">The token a stored row holds.</param>
    /// <param name="reason">The reason, when the token is known.</param>
    /// <returns><see langword="true"/> when the token names a reason.</returns>
    public static bool TryParse(string? token, out ConversationEndReason reason)
    {
        switch (token)
        {
            case "caller.hangup": reason = ConversationEndReason.CallerHungUp; return true;
            case "agent.completed": reason = ConversationEndReason.AgentCompleted; return true;
            case "agent.transferred": reason = ConversationEndReason.TransferredToHuman; return true;
            case "conversation.faulted": reason = ConversationEndReason.Faulted; return true;
            default: reason = default; return false;
        }
    }
}
