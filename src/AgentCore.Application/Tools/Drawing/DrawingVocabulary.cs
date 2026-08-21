using System.Reflection;

namespace AgentCore.Application.Tools.Drawing;

/// <summary>The prose vocabulary the drawing agent is instructed with.</summary>
/// <remarks>
/// Prose rather than a JSON Schema: the shipped schema for the same 27 components is 19,355 bytes
/// and would ride every request of every turn of every agent that may draw. The prose is a third of
/// that and rides only the drawing call.
/// </remarks>
internal static class DrawingVocabulary
{
    private const string VocabularyResource = "AgentCore.Application.Tools.Drawing.vocabulary.md";

    private static readonly Lazy<string> Vocabulary = new(Read);

    /// <summary>The vocabulary.</summary>
    internal static string Text => Vocabulary.Value;

    private static string Read()
    {
        var assembly = typeof(DrawingVocabulary).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(VocabularyResource)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{VocabularyResource}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
