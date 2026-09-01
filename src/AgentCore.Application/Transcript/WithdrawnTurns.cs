namespace AgentCore.Application.Transcript;

/// <summary>The span of turns one withdrawal took, both ends included.</summary>
/// <param name="First">The lowest turn index withdrawn.</param>
/// <param name="Last">The highest turn index withdrawn.</param>
internal readonly record struct WithdrawnTurns(int First, int Last);
