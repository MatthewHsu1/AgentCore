namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A voice turn's timing is known.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Metric">Which reading it is.</param>
    /// <param name="Value">The measured time.</param>
    public sealed record TurnLatency(HookScope Scope, LatencyMetric Metric, TimeSpan Value) : HookNotice(Scope);
}
