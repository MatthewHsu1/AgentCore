using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// The second stage of the pipeline in section 6: <see cref="JsonNode"/> to records.
/// </summary>
/// <remarks>
/// <para>
/// Check 1 runs before this, so the shape is already known good and this stage only moves values
/// across. <see cref="JsonSerializer"/> does the moving, against the settings in
/// <see cref="ConfigurationJson"/>. What is left here is turning a reader failure back into the
/// section 8.7 form: one error carrying a JSON Pointer into the document.
/// </para>
/// <para>
/// Every failure still carries a pointer, because a hand-built node tree may reach the binder
/// without the check. For a document that came through <see cref="ConfigurationLoader"/> these
/// branches are unreachable: check 1 rejects the same inputs first, and says more about them.
/// </para>
/// </remarks>
internal static partial class ConfigurationBinder
{
    internal static AgentCoreConfiguration Bind(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        try
        {
            return document.Deserialize<AgentCoreConfiguration>(ConfigurationJson.Options)
                   ?? throw Error(ConfigurationError.RootPointer, "the document is empty");
        }
        catch (JsonException exception)
        {
            throw Error(Pointer(exception.Path), Explain(exception.Message), exception);
        }
    }

    /// <summary>Turns the reader's <c>$.a.b[0]</c> path into the RFC 6901 pointer <c>/a/b/0</c>.</summary>
    /// <param name="path">The path the reader reported, or <see langword="null"/> when it reported none.</param>
    /// <returns>The pointer to the part of the document that failed.</returns>
    internal static string Pointer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '$')
        {
            return ConfigurationError.RootPointer;
        }

        var pointer = new StringBuilder();
        var index = 1;
        while (index < path.Length)
        {
            switch (path[index])
            {
                case '.':
                    // A plain property: everything up to the next separator.
                    index++;
                    var stop = path.AsSpan(index).IndexOfAny('.', '[');
                    var end = stop < 0 ? path.Length : index + stop;
                    Append(pointer, path[index..end]);
                    index = end;
                    break;

                case '[' when index + 1 < path.Length && path[index + 1] == '\'':
                    // A property the reader had to quote, because it holds a separator of its own.
                    var close = path.IndexOf("']", index + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return pointer.ToString();
                    }

                    Append(pointer, path[(index + 2)..close]);
                    index = close + 2;
                    break;

                case '[':
                    // An array index.
                    var bracket = path.IndexOf(']', index + 1);
                    if (bracket < 0)
                    {
                        return pointer.ToString();
                    }

                    Append(pointer, path[(index + 1)..bracket]);
                    index = bracket + 1;
                    break;

                default:
                    return pointer.ToString();
            }
        }

        return pointer.ToString();

        static void Append(StringBuilder pointer, string segment)
            => pointer.Append(ConfigurationError.AppendPointer(string.Empty, segment));
    }

    /// <summary>Rewrites the reader's message as one the author of a document can act on.</summary>
    /// <param name="message">The message the reader wrote.</param>
    /// <returns>The message the error carries.</returns>
    /// <remarks>
    /// The reader names CLR types and repeats the path it already reported through
    /// <see cref="JsonException.Path"/>. Both are noise to whoever wrote the document, so the tail is
    /// cut and the four kinds of value a configuration document can hold are named the way the
    /// schema names them. A message this does not recognise is passed through, so nothing is lost.
    /// </remarks>
    internal static string Explain(string message)
    {
        // The reader appends " Path: $.a.b | LineNumber: 0 | BytePositionInLine: 12." to its own
        // messages and to any a converter throws. The pointer already says where.
        var tail = message.IndexOf(" Path: $", StringComparison.Ordinal);
        if (tail >= 0)
        {
            message = message[..tail];
        }

        if (MissingPropertiesPattern().Match(message) is { Success: true } missing)
        {
            var names = missing.Groups[1].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            return names.Length == 1
                ? $"the required property {names[0]} is missing"
                : $"the required properties {string.Join(", ", names)} are missing";
        }

        if (WrongTypePattern().Match(message) is { Success: true } wrong)
        {
            // An optional key is a Nullable<T>, and the author of the document only cares about T.
            var type = NullableTypePattern().Match(wrong.Groups[1].Value) is { Success: true } wrapped
                ? wrapped.Groups[1].Value
                : wrong.Groups[1].Value;

            return type switch
            {
                "System.String" => "a string is expected",
                "System.Boolean" => "a boolean is expected",
                "System.Int32" or "System.Int64" => "a whole number is expected",
                "System.Double" or "System.Decimal" or "System.Single" => "a number is expected",
                _ => string.Create(CultureInfo.InvariantCulture, $"the value does not fit a '{type}'"),
            };
        }

        return message;
    }

    private static ConfigurationLoadException Error(string pointer, string message, Exception? cause = null)
        => new(
            new ConfigurationError
            {
                Pointer = pointer,
                Message = message,
                Check = ConfigurationCheck.DocumentSchema,
            },
            cause);

    [GeneratedRegex(@"was missing required properties,? including: (.+?)\.?$")]
    private static partial Regex MissingPropertiesPattern();

    [GeneratedRegex(@"^The JSON value could not be converted to (.+?)\.?$")]
    private static partial Regex WrongTypePattern();

    [GeneratedRegex(@"^System\.Nullable`1\[(.+)\]$")]
    private static partial Regex NullableTypePattern();
}
