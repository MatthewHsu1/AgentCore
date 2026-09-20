namespace AgentCore.Application.Runtime.Compaction;

/// <summary>The fixed constants of the one compaction every agent runs. See D7, D9, D15.</summary>
internal static class CompactionDefaults
{
    /// <summary>Groups for the summary, turns for the cap. The newest ones a stage never touches.</summary>
    public const int Keep = 4;

    /// <summary>Characters. What a capped tool result keeps.</summary>
    public const int CapResultChars = 200;

    /// <summary>Fraction of the model's context window at which compaction fires.</summary>
    public const double FireFraction = 0.75;

    /// <summary>Fraction of the model's context window the summary stops at.</summary>
    public const double TargetFraction = 0.5;
}
