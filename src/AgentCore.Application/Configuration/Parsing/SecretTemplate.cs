using System.Text;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// A configuration string that may hold <c>${secret:name}</c> references.
/// </summary>
/// <remarks>
/// Section 8.1 shows one in <c>tools[].request.headers</c>. The template keeps the raw text and the
/// references it found. It resolves nothing: <c>ISecretResolverPort</c> does that at startup.
/// </remarks>
public sealed record SecretTemplate
{
    private SecretTemplate(string raw, EquatableList<SecretReference> references)
    {
        Raw = raw;
        References = references;
    }

    /// <summary>Gets the text exactly as the document wrote it.</summary>
    public string Raw { get; }

    /// <summary>Gets the references the text holds, in the order they appear.</summary>
    public EquatableList<SecretReference> References { get; }

    /// <summary>Reports whether the text holds at least one reference.</summary>
    public bool HasSecretReferences => References.Count > 0;

    /// <summary>Reads a string and finds every reference it holds.</summary>
    /// <param name="text">The raw configuration string.</param>
    /// <returns>The template.</returns>
    public static SecretTemplate Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<SecretReference>? references = null;
        var index = 0;
        while (true)
        {
            var start = text.IndexOf(SecretReference.Prefix, index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var nameStart = start + SecretReference.Prefix.Length;
            var end = text.IndexOf('}', nameStart);
            if (end < 0)
            {
                break;
            }

            var name = text[nameStart..end];
            if (name.Length > 0)
            {
                references ??= [];
                references.Add(new SecretReference(name, start, end - start + 1));
            }

            index = end + 1;
        }

        return new SecretTemplate(
            text,
            references is null ? EquatableList<SecretReference>.Empty : new EquatableList<SecretReference>(references));
    }

    /// <summary>Rebuilds the text with every reference replaced by its resolved value.</summary>
    /// <param name="resolve">A function from a secret name to its value.</param>
    /// <returns>The text with no reference left in it.</returns>
    public string Format(Func<string, string> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        if (References.Count == 0)
        {
            return Raw;
        }

        var builder = new StringBuilder(Raw.Length);
        var cursor = 0;
        foreach (var reference in References)
        {
            builder.Append(Raw, cursor, reference.Start - cursor);
            builder.Append(resolve(reference.Name));
            cursor = reference.Start + reference.Length;
        }

        builder.Append(Raw, cursor, Raw.Length - cursor);
        return builder.ToString();
    }

    /// <summary>Writes the raw text.</summary>
    /// <returns>The raw text.</returns>
    public override string ToString() => Raw;
}
