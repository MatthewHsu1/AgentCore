namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A skill was pinned or loaded.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Name">The skill's name.</param>
    /// <param name="Pinned">Whether the entry pins the skill, as opposed to the model loading it.</param>
    public sealed record SkillLoaded(HookScope Scope, string Name, bool Pinned) : HookNotice(Scope);
}
