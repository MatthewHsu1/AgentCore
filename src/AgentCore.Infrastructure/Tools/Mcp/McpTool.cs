using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;

namespace AgentCore.Infrastructure.Tools.Mcp;

/// <summary>
/// One tool an MCP server serves, under the id the document gives it.
/// </summary>
internal sealed class McpTool : AIFunction
{
    private readonly McpServerSession _session;

    private readonly McpToolDescriptor _descriptor;

    private readonly string _id;

    /// <summary>Creates the tool.</summary>
    /// <param name="session">The server this tool is called on.</param>
    /// <param name="descriptor">The tool as the server described it when the session opened.</param>
    /// <param name="id">The served id: <c>&lt;server&gt;.&lt;tool&gt;</c>, or the <c>as:</c> alias.</param>
    public McpTool(McpServerSession session, McpToolDescriptor descriptor, string id)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(id);

        _session = session;
        _descriptor = descriptor;
        _id = id;
    }

    /// <inheritdoc />
    public override string Name => _id;

    /// <inheritdoc />
    public override string Description => _descriptor.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _descriptor.JsonSchema;

    /// <inheritdoc />
    /// <remarks>
    /// Section 8.7: this returns an error result and never throws. The caller is on a telephone call,
    /// and an exception would end the turn where a result lets the model say something.
    /// </remarks>
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            return await _session.CallAsync(_descriptor.Name, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (McpToolGoneException)
        {
            return Failed(
                $"the MCP server '{_session.Id}' no longer offers this tool. It was offered when this "
                + "service started, and the server has since withdrawn it.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"the call to the MCP server '{_session.Id}' ran out of time.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed($"the MCP server '{_session.Id}' could not be reached: {ex.GetBaseException().Message}");
        }
    }

    private JsonObject Failed(string message) => ToolErrorResult.Create(_id, message);
}
