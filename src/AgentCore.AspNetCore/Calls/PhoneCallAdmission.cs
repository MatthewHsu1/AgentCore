using AgentCore.Application.Hooks.Gates;

namespace AgentCore.AspNetCore.Calls
{
    /// <summary>
    /// The call gate's verdict on one offered call, after the session open. An admitted call must reach
    /// <see cref="PhoneCall.StartAsync"/> or <see cref="PhoneCall.AbandonAsync"/>: until then it keeps its conversation busy.
    /// </summary>
    /// <param name="Call">The admitted call, or <see langword="null"/>.</param>
    /// <param name="Refusal">Why the call is refused, or <see langword="null"/> when it was admitted.</param>
    internal sealed record PhoneCallAdmission(PhoneCall? Call, CallRefusal? Refusal)
    {
        /// <summary>Gets whether the call was refused as busy because another call holds its conversation.</summary>
        internal bool HeldByCall { get; private init; }

        internal static PhoneCallAdmission Admitted(PhoneCall call)
        {
            return new(call, null);
        }

        internal static PhoneCallAdmission Refused(CallRefusal refusal)
        {
            return new(null, refusal);
        }

        internal static PhoneCallAdmission RefusedHeldByCall()
        {
            return new(null, CallRefusal.Busy) { HeldByCall = true };
        }
    }
}
