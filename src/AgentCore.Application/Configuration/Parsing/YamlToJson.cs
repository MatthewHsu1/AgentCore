using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// The first stage of the pipeline in section 6: YamlDotNet to <see cref="JsonNode"/>. This is D20.
/// </summary>
/// <remarks>
/// <para>
/// A plain scalar takes its type from the YAML 1.2 core schema, so <c>3</c> and <c>0x10</c> become
/// numbers, <c>true</c> becomes a boolean, and <c>gpt-4.1-mini</c>, <c>yes</c> and <c>2024-01-01</c>
/// stay strings. A quoted, literal, or folded scalar always stays a string, and an explicit
/// <c>!!str</c> tag forces one. This is what makes rule 17 of section 11 hold: the same document
/// produces the same node tree as YAML and as JSON.
/// </para>
/// <para>
/// <c>Yaml2JsonNode</c>, by the maintainer of the JsonSchema.Net and JsonLogic packages this
/// assembly already carries, was measured against this file and does not resolve the core schema.
/// It reads a plain scalar with <c>decimal.TryParse(NumberStyles.Any)</c> and ignores the tag, so
/// <c>0x10</c> becomes the string "0x10", <c>!!str 1</c> becomes the number 1, and — the reason it
/// was not adopted — <c>1,000</c> becomes the number 1000 and <c>(5)</c> becomes the number -5,
/// which turns a typo in a customer document into a value that passes check 1. See the Task 5 row
/// of docs/handoff/library-first-cleanup.md.
/// </para>
/// </remarks>
public static partial class YamlToJson
{
    private const string YamlTagPrefix = "tag:yaml.org,2002:";

    /// <summary>Reads a YAML document and returns it as a JSON node tree.</summary>
    /// <param name="yaml">The YAML text of exactly one document.</param>
    /// <returns>The node tree.</returns>
    /// <exception cref="ConfigurationLoadException">The text is not one well-formed YAML document.</exception>
    public static JsonNode Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException exception)
        {
            throw Syntax(
                ConfigurationError.RootPointer,
                $"the document is not well-formed YAML at line {exception.Start.Line}, column {exception.Start.Column}: {exception.Message}",
                exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            // The scanner reports a few malformed inputs through a plain framework exception.
            throw Syntax(
                ConfigurationError.RootPointer,
                $"the document is not well-formed YAML: {exception.Message}",
                exception);
        }

        if (stream.Documents.Count != 1)
        {
            throw Syntax(
                ConfigurationError.RootPointer,
                $"the text holds {stream.Documents.Count} YAML documents, and exactly one is expected");
        }

        return Convert(stream.Documents[0].RootNode, ConfigurationError.RootPointer)
               ?? throw Syntax(ConfigurationError.RootPointer, "the document is empty");
    }

    private static JsonNode? Convert(YamlNode node, string pointer) => node switch
    {
        YamlMappingNode mapping => ConvertMapping(mapping, pointer),
        YamlSequenceNode sequence => ConvertSequence(sequence, pointer),
        YamlScalarNode scalar => ConvertScalar(scalar, pointer),
        _ => throw Syntax(pointer, "the node kind is not supported here"),
    };

    private static JsonObject ConvertMapping(YamlMappingNode mapping, string pointer)
    {
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode key || key.Value is null)
            {
                throw Syntax(pointer, "a mapping key must be a scalar");
            }

            var childPointer = ConfigurationError.AppendPointer(pointer, key.Value);

            // YamlDotNet rejects two keys that are the same YAML node, so this looks redundant. It is
            // not: two keys that differ as YAML nodes can still be the same JSON key. `!!str 1:` and
            // `1:` carry different tags, so the YAML reader keeps both, and both name the property
            // "1" here. Without this the second would silently overwrite the first.
            if (result.ContainsKey(key.Value))
            {
                throw Syntax(childPointer, $"the key '{key.Value}' appears twice in the same mapping");
            }

            result[key.Value] = Convert(pair.Value, childPointer);
        }

        return result;
    }

    private static JsonArray ConvertSequence(YamlSequenceNode sequence, string pointer)
    {
        var result = new JsonArray();
        var index = 0;
        foreach (var child in sequence.Children)
        {
            result.Add(Convert(child, ConfigurationError.AppendPointer(pointer, index)));
            index++;
        }

        return result;
    }

    private static JsonValue? ConvertScalar(YamlScalarNode scalar, string pointer)
    {
        var text = scalar.Value ?? string.Empty;
        var tag = scalar.Tag.IsEmpty ? null : scalar.Tag.Value;

        if (tag is not null && tag.StartsWith(YamlTagPrefix, StringComparison.Ordinal))
        {
            return ConvertTagged(tag, text, pointer);
        }

        if (tag is not null || scalar.Style is not ScalarStyle.Plain and not ScalarStyle.Any and not ScalarStyle.ForcePlain)
        {
            // A quoted, literal, folded, or custom-tagged scalar is always a string.
            return JsonValue.Create(text);
        }

        return ConvertPlain(text, pointer);
    }

    private static JsonValue? ConvertTagged(string tag, string text, string pointer) => tag switch
    {
        YamlTagPrefix + "str" => JsonValue.Create(text),
        YamlTagPrefix + "bool" => JsonValue.Create(ParseBool(text, pointer)),
        YamlTagPrefix + "int" => ParseInteger(text) ?? throw Syntax(pointer, $"'{text}' is not an integer"),
        YamlTagPrefix + "float" => ParseFloat(text, pointer) ?? throw Syntax(pointer, $"'{text}' is not a number"),
        YamlTagPrefix + "null" => null,
        _ => throw Syntax(pointer, $"the tag '{tag}' is not supported"),
    };

    private static JsonValue? ConvertPlain(string text, string pointer)
    {
        if (NullPattern().IsMatch(text))
        {
            return null;
        }

        if (TruePattern().IsMatch(text))
        {
            return JsonValue.Create(true);
        }

        if (FalsePattern().IsMatch(text))
        {
            return JsonValue.Create(false);
        }

        return ParseInteger(text) ?? ParseFloat(text, pointer) ?? JsonValue.Create(text);
    }

    private static bool ParseBool(string text, string pointer)
    {
        if (TruePattern().IsMatch(text))
        {
            return true;
        }

        if (FalsePattern().IsMatch(text))
        {
            return false;
        }

        throw Syntax(pointer, $"'{text}' is not a boolean");
    }

    private static JsonValue? ParseInteger(string text)
    {
        if (DecimalIntegerPattern().IsMatch(text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return JsonValue.Create(value);
        }

        if (HexIntegerPattern().IsMatch(text)
            && long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return JsonValue.Create(hex);
        }

        return null;
    }

    /// <summary>Reads a plain scalar that looks like a YAML 1.2 float.</summary>
    /// <param name="text">The scalar text.</param>
    /// <param name="pointer">The pointer to the scalar, for the error.</param>
    /// <returns>The number, or <see langword="null"/> when the text is not a float and stays a string.</returns>
    /// <remarks>
    /// A number too large for a <see cref="double"/> parses to an infinity, and JSON has no way to
    /// write one. Left alone it would reach <c>JsonValue.Create</c> and come back out of the loader
    /// as a raw <see cref="ArgumentException"/>, past the <see cref="ConfigurationLoadException"/>
    /// every caller of section 8.7 is told to catch. It is a defect in the document, so it is
    /// reported as one.
    /// </remarks>
    private static JsonValue? ParseFloat(string text, string pointer)
    {
        if (!FloatPattern().IsMatch(text)
            || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return double.IsFinite(value)
            ? JsonValue.Create(value)
            : throw Syntax(pointer, $"'{text}' is larger than a number can hold");
    }

    private static ConfigurationLoadException Syntax(string pointer, string message, Exception? cause = null)
        => new(
            new ConfigurationError
            {
                Pointer = pointer,
                Message = message,
                Check = ConfigurationCheck.Syntax,
            },
            cause);

    [GeneratedRegex(@"^(null|Null|NULL|~)?$")]
    private static partial Regex NullPattern();

    [GeneratedRegex("^(true|True|TRUE)$")]
    private static partial Regex TruePattern();

    [GeneratedRegex("^(false|False|FALSE)$")]
    private static partial Regex FalsePattern();

    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex DecimalIntegerPattern();

    [GeneratedRegex("^0[xX][0-9a-fA-F]+$")]
    private static partial Regex HexIntegerPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatPattern();
}
