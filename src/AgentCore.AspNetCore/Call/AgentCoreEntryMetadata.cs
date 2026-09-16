namespace AgentCore.AspNetCore.Call;

/// <summary>Which entry one mapped route answers on.</summary>
/// <param name="Entry">The entry key the host named.</param>
/// <param name="Surface">The route family: Call, Responses, or TelnyxRelay.</param>
internal sealed record AgentCoreEntryMetadata(string Entry, string Surface);
