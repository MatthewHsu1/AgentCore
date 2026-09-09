namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The two speech roles: who turns sound into text, and who turns text back into sound.
/// </summary>
public sealed record SpeechProviderConfiguration
{
    /// <summary>Gets the recognition vendor: what the caller said, turned into text.</summary>
    public required VendorProviderConfiguration Stt { get; init; }

    /// <summary>Gets the synthesis vendor: text, turned into what the caller hears.</summary>
    public required VendorProviderConfiguration Tts { get; init; }
}
