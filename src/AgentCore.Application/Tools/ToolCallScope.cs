namespace AgentCore.Application.Tools;

/// <summary>The call a bound tool runs in. A binding declares a parameter of this type to receive it; the model never sees that parameter.</summary>
/// <param name="CallId">The call the turn belongs to.</param>
/// <param name="TurnIndex">The zero-based index of the turn now running.</param>
/// <param name="Stage">The stage the call's state machine holds. Empty when the document declares no policy.</param>
/// <param name="Workspace">The call's folder on disk, or <see langword="null"/> when the host bound no workspace root.</param>
public sealed record ToolCallScope(string CallId, int TurnIndex, string Stage, string? Workspace = null);
