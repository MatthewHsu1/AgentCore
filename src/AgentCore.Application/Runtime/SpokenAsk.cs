using AgentCore.Application.State;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Whether a turn's own reply actually put the question channel 1 staged for it.
/// </summary>
internal static class SpokenAsk
{
    /// <summary>Whether <paramref name="reply"/> put the question <paramref name="named"/> staged.</summary>
    /// <param name="reply">The words the caller heard.</param>
    /// <param name="named">What the staged ask offered.</param>
    /// <returns>
    /// <see langword="true"/> where the reply names one of the offered values. A record naming no
    /// values — the over-cap form, whose instruction lists none — carries nothing to look for, so it
    /// counts as asked rather than being dropped on every turn alike.
    /// </returns>
    internal static bool NamedIn(string reply, Clarifications.LastNamed named)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if (named.Kind != Clarifications.LastNamedKind.Set || named.Values is not { } values)
        {
            return true;
        }

        var folded = VocabularyFold.Fold(reply);

        foreach (var value in values)
        {
            var candidate = VocabularyFold.Fold(value);

            if (candidate.Length > 0 && folded.Contains(candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
