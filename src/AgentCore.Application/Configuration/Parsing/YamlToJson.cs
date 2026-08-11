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
/// A plain scalar takes its type from the YAML 1.2 core schema, so <c>3</c> becomes a number,
/// <c>true</c> becomes a boolean, and <c>gpt-4.1-mini</c> stays a string. A quoted, literal, or
/// folded scalar always stays a string. This is what makes rule 17 of section 11 hold: the same
/// document produces the same node tree as YAML and as JSON.
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

        return ConvertPlain(text);
    }

    private static JsonValue? ConvertTagged(string tag, string text, string pointer) => tag switch
    {
        YamlTagPrefix + "str" => JsonValue.Create(text),
        YamlTagPrefix + "bool" => JsonValue.Create(ParseBool(text, pointer)),
        YamlTagPrefix + "int" => ParseInteger(text) ?? throw Syntax(pointer, $"'{text}' is not an integer"),
        YamlTagPrefix + "float" => ParseFloat(text) ?? throw Syntax(pointer, $"'{text}' is not a number"),
        YamlTagPrefix + "null" => null,
        _ => throw Syntax(pointer, $"the tag '{tag}' is not supported"),
    };

    private static JsonValue? ConvertPlain(string text)
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

        return ParseInteger(text) ?? ParseFloat(text) ?? JsonValue.Create(text);
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

    private static JsonValue? ParseFloat(string text)
    {
        if (FloatPattern().IsMatch(text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return JsonValue.Create(value);
        }

        return null;
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
