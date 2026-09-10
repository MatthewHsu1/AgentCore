using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// Refuses a <c>scope.filterable</c> block the search tool could not be built from.
/// </summary>
internal static class KnowledgeFilterValidator
{
    private const string Pointer = "/providers/knowledge/scope/filterable";

    /// <summary>Checks the block, adding one error per fault.</summary>
    /// <param name="scope">The document's <c>providers.knowledge.scope</c> block, or <see langword="null"/>.</param>
    /// <param name="errors">Where the faults go.</param>
    /// <exception cref="ArgumentNullException"><paramref name="errors"/> is <see langword="null"/>.</exception>
    internal static void Check(KnowledgeScopeConfiguration? scope, List<ConfigurationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (scope?.Filterable is not { Count: > 0 } filterable)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(scope.Template))
        {
            errors.Add(Reference(
                Pointer,
                "filterable names facets and scope.template is not set, so there is no payload path "
                + "to resolve a facet key to. Set the template, or drop filterable."));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        for (var index = 0; index < filterable.Count; index++)
        {
            var facet = filterable[index];
            var pointer = ConfigurationError.AppendPointer(Pointer, index);

            if (string.IsNullOrWhiteSpace(facet.Key))
            {
                errors.Add(Reference(pointer, "the facet key is blank, so it names no facet."));
            }
            else if (!seen.Add(facet.Key))
            {
                errors.Add(Reference(
                    pointer,
                    $"the facet key '{facet.Key}' is named twice. One key is one choice in the "
                    + "search tool's filters argument, and a repeat offers the model the same choice "
                    + "again."));
            }

            if (string.IsNullOrWhiteSpace(facet.Description))
            {
                errors.Add(Reference(
                    pointer,
                    $"the facet '{facet.Key}' has no description. The description is the only thing "
                    + "the model reads before it picks a value, so a facet without one is a key it "
                    + "can only guess at."));
            }
        }
    }

    private static ConfigurationError Reference(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.ReferenceResolution };
}
