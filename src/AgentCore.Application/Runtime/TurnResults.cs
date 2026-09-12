using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// What the tools the model called this turn answered, kept so a script can read the rows instead of
/// a model retyping them.
/// </summary>
internal sealed class TurnResults
{
    /// <summary>The key under which every result of one tool sits, oldest first.</summary>
    internal const string AllKey = "$all";

    private readonly Lock _gate = new();

    private readonly Dictionary<string, List<JsonNode>> _byTool = new(StringComparer.Ordinal);

    private readonly List<string> _order = [];

    /// <summary>The tools that answered something structured, in first-answer order.</summary>
    internal IReadOnlyList<string> Tools
    {
        get
        {
            lock (_gate)
            {
                return [.. _order];
            }
        }
    }

    /// <summary>Keeps one tool's answer, when it is structured.</summary>
    /// <param name="tool">The tool's declared id.</param>
    /// <param name="result">What it returned.</param>
    internal void Record(string tool, object? result)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (AsStructured(result) is not { } node)
        {
            return;
        }

        lock (_gate)
        {
            if (!_byTool.TryGetValue(tool, out var answers))
            {
                answers = [];
                _byTool[tool] = answers;
                _order.Add(tool);
            }

            answers.Add(node);
        }
    }

    /// <summary>The latest answer of one tool, or <see langword="null"/>.</summary>
    /// <param name="tool">The tool's declared id.</param>
    /// <returns>A copy of the answer.</returns>
    internal JsonNode? Latest(string tool)
    {
        lock (_gate)
        {
            return _byTool.TryGetValue(tool, out var answers) ? answers[^1].DeepClone() : null;
        }
    }

    /// <summary>
    /// What a script sees as <c>data</c>: each tool's latest answer under its id, and every answer
    /// under <see cref="AllKey"/>.
    /// </summary>
    /// <returns>A fresh object each time. A script may do what it likes to it.</returns>
    internal JsonObject Data()
    {
        lock (_gate)
        {
            JsonObject data = [];
            JsonObject all = [];
            foreach (var tool in _order)
            {
                var answers = _byTool[tool];
                data[tool] = answers[^1].DeepClone();
                all[tool] = new JsonArray([.. answers.Select(answer => answer.DeepClone())]);
            }

            data[AllKey] = all;
            return data;
        }
    }

    private static JsonNode? AsStructured(object? result)
    {
        var node = result switch
        {
            null => null,
            JsonNode direct => direct.DeepClone(),
            JsonElement element => JsonSerializer.SerializeToNode(element),
            string text => Parse(text),
            _ => JsonSerializer.SerializeToNode(result, result.GetType(), AIJsonUtilities.DefaultOptions),
        };

        return node is JsonObject or JsonArray && !(node is JsonObject error && ToolErrorResult.IsError(error))
            ? node
            : null;
    }

    private static JsonNode? Parse(string text)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
