namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// One reason a configuration document did not load.
/// </summary>
/// <remarks>
/// Section 8.7: loading fails fast, and every message carries a JSON Pointer into the document.
/// All eight checks of section 8.5 report through this record.
/// </remarks>
public sealed record ConfigurationError
{
    /// <summary>The JSON Pointer to the whole document.</summary>
    public const string RootPointer = "";

    /// <summary>Gets the RFC 6901 JSON Pointer to the part of the document that failed.</summary>
    public required string Pointer { get; init; }

    /// <summary>Gets the message a human reads. It does not repeat the pointer.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the check that produced the error.</summary>
    public required ConfigurationCheck Check { get; init; }

    /// <summary>Writes the pointer and the message on one line.</summary>
    /// <returns>The text the exception message holds.</returns>
    public override string ToString()
        => $"{(Pointer.Length == 0 ? "/" : Pointer)}: {Message} (check {(int)Check})";

    /// <summary>Escapes one JSON Pointer segment, then appends it to a pointer.</summary>
    /// <param name="pointer">The pointer to extend.</param>
    /// <param name="segment">The raw property name or array index.</param>
    /// <returns>The extended pointer.</returns>
    public static string AppendPointer(string pointer, string segment)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(segment);

        return pointer + "/" + segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>Appends an array index to a pointer.</summary>
    /// <param name="pointer">The pointer to extend.</param>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The extended pointer.</returns>
    public static string AppendPointer(string pointer, int index)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        return pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
