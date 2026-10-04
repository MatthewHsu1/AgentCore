namespace AgentCore.Application.Hooks
{
    /// <summary>
    /// One of the 13 points where AgentCore awaits its hooks. The point, not the hook, fixes the deadline and the
    /// default failure policy.
    /// </summary>
    public sealed class GatePoint
    {
        private GatePoint(string name, TimeSpan deadline, HookFailure defaultFailure)
        {
            Name = name;
            Deadline = deadline;
            DefaultFailure = defaultFailure;
            MethodName = name + "Async";
        }

        /// <summary>Gets the gate where a transport offers a call. Fails closed: the call is rejected as unavailable.</summary>
        public static GatePoint BeforeCall { get; } = new(nameof(BeforeCall), TimeSpan.FromSeconds(5), HookFailure.Closed);

        /// <summary>Gets the gate where a request needs an entry. Fails closed: the request is refused.</summary>
        public static GatePoint BeforeEntry { get; } = new(nameof(BeforeEntry), TimeSpan.FromSeconds(2), HookFailure.Closed);

        /// <summary>Gets the gate where the user's message arrives, before moderation and the model.</summary>
        public static GatePoint BeforeTurn { get; } = new(nameof(BeforeTurn), TimeSpan.FromSeconds(2), HookFailure.Open);

        /// <summary>Gets the gate once per run, when its context is built.</summary>
        public static GatePoint BeforeRun { get; } = new(nameof(BeforeRun), TimeSpan.FromSeconds(2), HookFailure.Open);

        /// <summary>Gets the gate where AgentCore is about to compact.</summary>
        public static GatePoint BeforeCompaction { get; } = new(nameof(BeforeCompaction), TimeSpan.FromSeconds(5), HookFailure.Open);

        /// <summary>Gets the gate before each model round trip.</summary>
        public static GatePoint BeforeModel { get; } = new(nameof(BeforeModel), TimeSpan.FromSeconds(1), HookFailure.Open);

        /// <summary>Gets the gate after each round trip, before tools run or text leaves.</summary>
        public static GatePoint AfterModel { get; } = new(nameof(AfterModel), TimeSpan.FromSeconds(1), HookFailure.Open);

        /// <summary>Gets the gate where a round trip threw.</summary>
        public static GatePoint AfterModelFailed { get; } = new(nameof(AfterModelFailed), TimeSpan.FromSeconds(1), HookFailure.Open);

        /// <summary>Gets the gate where a tool that needs approval was called.</summary>
        public static GatePoint BeforeToolApproval { get; } = new(nameof(BeforeToolApproval), TimeSpan.FromSeconds(2), HookFailure.Open);

        /// <summary>Gets the gate before each tool call.</summary>
        public static GatePoint BeforeTool { get; } = new(nameof(BeforeTool), TimeSpan.FromSeconds(2), HookFailure.Open);

        /// <summary>Gets the gate after a tool returned.</summary>
        public static GatePoint AfterTool { get; } = new(nameof(AfterTool), TimeSpan.FromSeconds(2), HookFailure.Open);

        /// <summary>Gets the gate where a tool threw.</summary>
        public static GatePoint AfterToolFailed { get; } = new(nameof(AfterToolFailed), TimeSpan.FromSeconds(2), HookFailure.Open);

        /// <summary>Gets the gate where the run has a reply, before the turn is sealed.</summary>
        public static GatePoint AfterRun { get; } = new(nameof(AfterRun), TimeSpan.FromSeconds(2), HookFailure.Open);

        /// <summary>Gets every gate point, in gate order.</summary>
        public static IReadOnlyList<GatePoint> All { get; } =
        [
            BeforeCall, BeforeEntry, BeforeTurn, BeforeRun, BeforeCompaction, BeforeModel, AfterModel,
            AfterModelFailed, BeforeToolApproval, BeforeTool, AfterTool, AfterToolFailed, AfterRun,
        ];

        /// <summary>Gets the point's name, which is its <see cref="AgentHook"/> method without <c>Async</c>.</summary>
        public string Name { get; }

        /// <summary>Gets how long one hook may take before it is abandoned.</summary>
        public TimeSpan Deadline { get; }

        /// <summary>Gets what a failing hook does unless it overrides <see cref="AgentHook.FailureFor"/>.</summary>
        public HookFailure DefaultFailure { get; }

        /// <summary>Gets the <see cref="AgentHook"/> method this point calls.</summary>
        internal string MethodName { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Name;
        }
    }
}
