namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>
/// The frames Telnyx sends, as the vendor page prints them.
/// </summary>
/// <remarks>
/// The bodies are golden inputs, so a test's expectations trace back to §7.1 and the frame reader
/// this project already ships. A test that needs a different shape writes the JSON itself rather
/// than adding a parameter here, so this file stays a record of the real wire.
/// </remarks>
internal static class RelayFrames
{
    /// <summary>Builds the first frame of a call.</summary>
    public static string Setup(string callSessionId = "call-one", string callControlId = "v2:leg-one")
        => $$"""
        {"type":"setup","sessionId":"session-one","accountSid":"account-one",
         "callSid":"{{callControlId}}","callControlId":"{{callControlId}}",
         "callSessionId":"{{callSessionId}}","callLegId":"leg-one",
         "from":"+13122010094","to":"+13122123456","direction":"inbound",
         "callerName":"","customParameters":{},"callStatus":"active"}
        """;

    /// <summary>Builds one transcript frame.</summary>
    /// <param name="text">What the caller said.</param>
    /// <param name="last">True for a final transcript, false for an interim one.</param>
    public static string Prompt(string text, bool last)
        => $$"""{"type":"prompt","voicePrompt":"{{text}}","lang":"en","last":{{(last ? "true" : "false")}}}""";

    /// <summary>Builds the truncation record.</summary>
    public static string Interrupt(string heard, int durationMs)
        => $$"""
        {"type":"interrupt","utteranceUntilInterrupt":"{{heard}}","durationUntilInterruptMs":{{durationMs}}}
        """;

    /// <summary>Builds one key press.</summary>
    public static string Dtmf(string digit)
        => $$"""{"type":"dtmf","digit":"{{digit}}"}""";

    /// <summary>Builds the frame the vendor sends when it refuses one of ours.</summary>
    public static string Error(string description)
        => $$"""{"type":"error","description":"{{description}}"}""";
}
