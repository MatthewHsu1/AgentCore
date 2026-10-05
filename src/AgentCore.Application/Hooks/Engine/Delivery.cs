namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>One item of a hook's channel: a notice, or a flush barrier that completes when the reader reaches it.</summary>
    /// <param name="Notice">The notice to hand the hook, or <see langword="null"/> for a barrier.</param>
    /// <param name="Barrier">Completed when the reader reaches this item, or <see langword="null"/> for a notice.</param>
    internal readonly record struct Delivery(HookNotice? Notice, TaskCompletionSource? Barrier);
}
