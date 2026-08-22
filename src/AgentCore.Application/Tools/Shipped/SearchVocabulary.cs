using System.Reflection;

namespace AgentCore.Application.Tools.Shipped;

/// <summary>The prose the agentic search agent is instructed with.</summary>
/// <remarks>
/// It is a resource rather than a string literal so that editing how the agent searches is not a
/// code change, and so the prose stays readable at the width it will be read at. It rides only the
/// inner call, never a turn of the outer agent.
/// </remarks>
internal static class SearchVocabulary
{
    private const string VocabularyResource = "AgentCore.Application.Tools.Shipped.search-vocabulary.md";

    private static readonly Lazy<string> Vocabulary = new(Read);

    /// <summary>The instructions.</summary>
    internal static string Text => Vocabulary.Value;

    private static string Read()
    {
        var assembly = typeof(SearchVocabulary).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(VocabularyResource)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{VocabularyResource}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
