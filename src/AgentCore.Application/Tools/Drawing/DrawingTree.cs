using System.Text.Json.Nodes;

namespace AgentCore.Application.Tools.Drawing;

/// <summary>Validates a drawing tree against the vocabulary the drawing model was given.</summary>
internal static class DrawingTree
{
    /// <summary>
    /// The component names a tree may use.
    /// </summary>
    /// <remarks>
    /// This is the security boundary the browser enforces as well: a <c>$type</c> outside the list
    /// renders nothing. It is checked here so the model is told, rather than the caller being shown
    /// a hole. It must agree with <c>vocabulary.md</c> and with the library the browser renders
    /// with; <c>DrawingAgentTests.TheVocabulary_TeachesEveryComponentTheValidatorAllows</c> and
    /// <c>GenerativeUiDataUI.test.tsx</c> pin both.
    /// </remarks>
    internal static readonly string[] AllowedComponents =
    [
        "Header", "Text", "Caption", "Image", "Divider", "Fact", "Button", "Select", "Input",
        "DatePicker", "Checkbox", "RadioGroup", "Form", "Card", "Col", "Row", "Spacer", "Badge",
        "Box", "ListView", "ListViewItem", "Table", "Markdown", "Chart", "Alert", "Carousel", "Icon",
    ];

    /// <summary>The keys that are not props. <c>vocabulary.md</c> names the same four.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "$type", "$key", "$action", "children",
    };

    /// <summary>Checks a tree against the vocabulary.</summary>
    /// <param name="node">The tree.</param>
    /// <returns>What is wrong, in words the model can act on, or <see langword="null"/> when it is sound.</returns>
    internal static string? Validate(JsonNode? node)
    {
        if (node is not JsonObject item)
        {
            return node is JsonArray
                ? "a node is an array where an object was wanted."
                : "a node is not an object.";
        }

        var rawType = item["$type"];
        if (ReadString(rawType) is not { Length: > 0 } type)
        {
            return rawType is null ? "a node has no $type." : "a node's $type is not a string.";
        }

        if (Array.IndexOf(AllowedComponents, type) < 0)
        {
            return $"'{type}' is not a component. Use one of: {string.Join(", ", AllowedComponents)}.";
        }

        if (ActionFault(item, type) is { } typeFault)
        {
            return typeFault;
        }

        if (item["children"] is { } children)
        {
            if (children is not JsonArray list)
            {
                return $"'children' on '{type}' is not an array.";
            }

            foreach (var child in list)
            {
                // A bare string is a valid child: the renderer draws it as text.
                if (child is JsonValue)
                {
                    continue;
                }

                if (Validate(child) is { } fault)
                {
                    return fault;
                }
            }
        }

        foreach (var pair in item)
        {
            if (Reserved.Contains(pair.Key))
            {
                continue;
            }

            if (ValidateActions(pair.Value, pair.Key) is { } fault)
            {
                return fault;
            }
        }

        return null;
    }

    /// <summary>Checks the <c>$action</c> of anything that is not a node.</summary>
    /// <param name="node">A prop value.</param>
    /// <param name="owner">The prop it was reached through, for the message.</param>
    /// <returns>What is wrong, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A prop can carry an <c>$action</c> without being a component: <c>Card.confirm</c> and
    /// <c>Card.cancel</c> are <c>{ "label", "$action" }</c>. This walks the same props the receipt
    /// collects, so nothing the receipt names goes unchecked. It asks for no <c>$type</c>, because
    /// most props holding objects — <c>Chart.data</c>, <c>Table.columns</c>, <c>Select.options</c> —
    /// are data rather than nodes.
    /// </remarks>
    private static string? ValidateActions(JsonNode? node, string owner)
    {
        switch (node)
        {
            case JsonArray list:
                foreach (var item in list)
                {
                    if (ValidateActions(item, owner) is { } fault)
                    {
                        return fault;
                    }
                }

                return null;

            case JsonObject bag:
                if (ActionFault(bag, owner) is { } bagFault)
                {
                    return bagFault;
                }

                foreach (var pair in bag)
                {
                    // Not into the $action itself: its payload is the model's own data.
                    if (string.Equals(pair.Key, "$action", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ValidateActions(pair.Value, owner) is { } fault)
                    {
                        return fault;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>Checks the <c>$action</c> one node or prop carries.</summary>
    /// <param name="node">The node or prop that may carry one.</param>
    /// <param name="owner">What to name in the message — a component name, or the prop it was reached through.</param>
    /// <returns>What is wrong, or <see langword="null"/> when there is no fault.</returns>
    private static string? ActionFault(JsonObject node, string owner)
        => node["$action"] is { } action
           && (action is not JsonObject shape || ReadString(shape["type"]) is not { Length: > 0 })
            ? $"the $action on '{owner}' has no 'type'."
            : null;

    /// <summary>
    /// Reads a node as a string without throwing.
    /// </summary>
    /// <remarks>
    /// A model can send a <c>$type</c> or <c>$action.type</c> of any JSON kind — a number, a bool, an
    /// object. <see cref="JsonNode.GetValue{T}"/> throws <see cref="InvalidOperationException"/> for
    /// any of those, and section 8.7 forbids that path: the wrong kind is a fault the model can act
    /// on, not a reason to end the turn.
    /// </remarks>
    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
