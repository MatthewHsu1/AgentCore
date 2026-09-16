using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

// Only the server's declared properties cross the wire (see _declared). The invoking client files
// the turn into every call's arguments and the turn carries delegates, which the MCP SDK's argument
// serializer cannot hold — forwarding everything faulted every call. Revisit if the SDK ever lets a
// call exclude entries, or if the turn stops riding along in tool arguments.

namespace AgentCore.Infrastructure.Tools.Mcp;

/// <summary>
/// One tool an MCP server serves, under the id the document gives it.
/// </summary>
internal sealed class McpTool : AIFunction
{
    private readonly McpServerSession _session;

    private readonly McpToolDescriptor _descriptor;

    private readonly string _id;

    private readonly HashSet<string>? _declared;

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
        _declared = DeclaredProperties(descriptor.JsonSchema);
    }

    /// <summary>Reads the property names one tool schema declares, if it declares any.</summary>
    /// <param name="schema">The schema the server listed for this tool.</param>
    /// <returns>The names, or <see langword="null"/> when the schema names none.</returns>
    private static HashSet<string>? DeclaredProperties(JsonElement schema)
    {
        if (schema.ValueKind is not JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            names.Add(property.Name);
        }

        return names;
    }

    /// <inheritdoc />
    public override string Name => _id;

    /// <inheritdoc />
    public override string Description => _descriptor.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _descriptor.JsonSchema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            // The model is built against this schema, so anything outside it is harness
            // residue rather than model intent: the filed turn carries delegates no JSON
            // writer can hold. A schema that names no properties keeps the old passthrough.
            var outgoing = arguments;
            if (_declared is { } declared)
            {
                outgoing = new AIFunctionArguments();
                foreach (var entry in arguments.Where(candidate => declared.Contains(candidate.Key)))
                {
                    outgoing[entry.Key] = entry.Value;
                }
            }

            var answer = await _session.CallAsync(_descriptor.Name, outgoing, cancellationToken)
                .ConfigureAwait(false);

            return Unwrap(answer);
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

    /// <summary>Reads the answer out of the envelope the protocol wraps it in.</summary>
    /// <param name="answer">What the server sent back.</param>
    /// <returns>The answer itself, or an error result when the tool failed on its own terms.</returns>
    private object? Unwrap(CallToolResult answer)
    {
        var text = TextOf(answer);

        // A tool that fails answers in the one error shape, whichever way it failed.
        // The protocol's own failure flag is not that shape, so it is carried into it here.
        if (answer.IsError == true)
        {
            return Failed(text is { Length: > 0 }
                ? $"the MCP server '{_session.Id}' answered with a failure: {text}"
                : $"the MCP server '{_session.Id}' answered with a failure and said nothing about it.");
        }

        // A server that fills the spec's own structured field means that to be the answer, so it
        // beats reading the same answer back out of the text blocks.
        return answer.StructuredContent is { } structured ? structured : text;
    }

    /// <summary>Joins the text blocks of one answer, and names every block that is not text.</summary>
    /// <param name="answer">What the server sent back.</param>
    /// <returns>The text, which is empty when the server sent no blocks.</returns>
    private static string TextOf(CallToolResult answer)
        => string.Join(
            "\n",
            answer.Content.Select(block => block is TextContentBlock text ? text.Text : $"[{block.Type}]"));

    private JsonObject Failed(string message) => ToolErrorResult.Create(_id, message);
}
