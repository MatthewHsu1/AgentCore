using System.Text.Json;

namespace AgentCore.Infrastructure.Tools.Mcp;

/// <summary>
/// One tool an MCP server offered, as it stood when the session opened.
/// </summary>
/// <remarks>
/// This is a copy, not a handle. An <see cref="ModelContextProtocol.Client.McpClientTool"/> is bound
/// to the one client that listed it, so holding one past a reconnect would hold a dead connection.
/// The session hands out these instead, and routes every call by <see cref="Name"/>.
/// </remarks>
/// <param name="Name">The name the server offers the tool under. It is never the served id.</param>
/// <param name="Description">The sentence the model reads.</param>
/// <param name="JsonSchema">The raw JSON Schema of the arguments, as the server wrote it.</param>
internal sealed record McpToolDescriptor(string Name, string Description, JsonElement JsonSchema);
