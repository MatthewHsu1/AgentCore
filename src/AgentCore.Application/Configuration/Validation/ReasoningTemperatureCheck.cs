using System.Globalization;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// Check 9 over the pair <c>providers.llm[].reasoningEffort</c> and <c>model.temperature</c>.
/// </summary>
/// <remarks>
/// <para>
/// A reasoning model samples at one temperature only. When the entry sets an effort above
/// <c>none</c>, the vendor answers 400 to any temperature other than 1, and the message names
/// <c>temperature</c> without naming the effort that made it illegal. The two keys sit in
/// different parts of the document, so the pair is refused here, where the error still carries a
/// pointer at the key the author has to change.
/// </para>
/// <para>
/// The check reads the document only. A caller that sets a temperature on its own call options in
/// code can still send one the entry forbids, because no configuration check can see that.
/// </para>
/// </remarks>
internal static class ReasoningTemperatureCheck
{
    /// <summary>The one temperature a reasoning entry accepts.</summary>
    private const double ReasoningTemperature = 1;

    /// <summary>The one <c>reasoningEffort</c> value that leaves temperature free.</summary>
    private const string NoReasoning = "none";

    /// <summary>Refuses every model reference whose temperature its provider entry forbids.</summary>
    /// <param name="configuration">The bound document.</param>
    /// <param name="errors">Receives one error for each offending reference.</param>
    public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        var reasoning = ReasoningEntries(configuration);
        if (reasoning.Count == 0)
        {
            return;
        }

        Check(configuration.Extractor?.Model, "/extractor/model", reasoning, errors);
        Check(configuration.Evaluation?.Judge, "/evaluation/judge", reasoning, errors);
        Check(configuration.Titler?.Model, "/titler/model", reasoning, errors);
        Check(configuration.Agents?.Defaults?.Model, "/agents/defaults/model", reasoning, errors);

        var agents = configuration.Agents?.Items ?? [];
        for (var index = 0; index < agents.Count; index++)
        {
            Check(agents[index].Model, Reference("/agents/items", index), reasoning, errors);
        }

        for (var index = 0; index < configuration.Tools.Count; index++)
        {
            Check(configuration.Tools[index].Model, Reference("/tools", index), reasoning, errors);
        }
    }

    /// <summary>Maps each <c>as</c> name that reasons to the effort it reasons at.</summary>
    /// <param name="configuration">The bound document.</param>
    /// <returns>The map, empty when no entry reasons.</returns>
    private static Dictionary<string, string> ReasoningEntries(AgentCoreConfiguration configuration)
    {
        Dictionary<string, string> reasoning = new(StringComparer.Ordinal);
        foreach (var entry in configuration.Providers?.Llm ?? [])
        {
            if (entry.ReasoningEffort is { Length: > 0 } effort
                && !string.Equals(effort, NoReasoning, StringComparison.OrdinalIgnoreCase))
            {
                reasoning[entry.As] = effort;
            }
        }

        return reasoning;
    }

    /// <summary>Refuses one model reference.</summary>
    /// <param name="model">The reference, or <see langword="null"/> when the document writes none.</param>
    /// <param name="pointer">The JSON Pointer to the reference itself, without a trailing key.</param>
    /// <param name="reasoning">The entries that reason, by <c>as</c> name.</param>
    /// <param name="errors">Receives the error.</param>
    private static void Check(
        ModelReference? model,
        string pointer,
        Dictionary<string, string> reasoning,
        List<ConfigurationError> errors)
    {
        if (model?.Temperature is not { } temperature
            || temperature == ReasoningTemperature
            || !reasoning.TryGetValue(model.Ref, out var effort))
        {
            return;
        }

        errors.Add(new ConfigurationError
        {
            Pointer = ConfigurationError.AppendPointer(pointer, "temperature"),
            Message = string.Format(
                CultureInfo.InvariantCulture,
                "temperature {0} is refused because the model '{1}' sets reasoningEffort '{2}' in "
                + "providers.llm. A reasoning entry accepts temperature {3} only. Remove temperature, "
                + "or set that entry's reasoningEffort to {4}.",
                temperature,
                model.Ref,
                effort,
                ReasoningTemperature,
                NoReasoning),
            Check = ConfigurationCheck.ValueRange,
        });
    }

    /// <summary>Points at the <c>model</c> of one item of a sequence.</summary>
    /// <param name="sequence">The JSON Pointer to the sequence.</param>
    /// <param name="index">The zero-based index of the item.</param>
    /// <returns>The pointer to that item's model reference.</returns>
    private static string Reference(string sequence, int index)
        => ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(sequence, index), "model");
}
