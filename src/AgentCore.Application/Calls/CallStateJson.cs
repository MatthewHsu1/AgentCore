using System.Text.Json;

namespace AgentCore.Application.Calls;

/// <summary>How <see cref="CallSessionState"/> is encoded. Every store agrees on it.</summary>
public static class CallStateJson
{
    /// <summary>The shared options.</summary>
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

        options.MakeReadOnly(populateMissingResolver: true);
        
        return options;
    }
}
