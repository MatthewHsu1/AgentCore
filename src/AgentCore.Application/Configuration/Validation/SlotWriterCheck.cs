using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>Check 3: one writer for each slot.</summary>
internal static class SlotWriterCheck
{
    /// <summary>The <c>increment:</c> field of a counter slot. Named once, so the check and the two maps under it cannot drift apart.</summary>
    private const string IncrementField = "increment";

    public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        foreach (var entry in configuration.State)
        {
            var pointer = ValidationPointer.State(entry.Key);
            var slot = entry.Value;

            if (ReservedStateSlots.Contains(entry.Key))
            {
                errors.Add(Writers(
                    pointer,
                    $"the slot has two writers: '{entry.Key}' is a reserved read-only slot that is always present, and state: declares it again"));
            }

            var owner = OwnerField(slot.Writer);
            if (owner is not null && FieldValue(slot, owner) is null)
            {
                errors.Add(Writers(
                    pointer,
                    $"the slot has zero writers: writer: {WriterName(slot.Writer)} fills the slot from '{owner}:', and the slot declares none"));
            }

            foreach (var field in new[] { "from", IncrementField, "value" })
            {
                if (string.Equals(field, owner, StringComparison.Ordinal) || FieldValue(slot, field) is null)
                {
                    continue;
                }

                errors.Add(Writers(
                    ConfigurationError.AppendPointer(pointer, field),
                    $"the slot has two writers: writer: {WriterName(slot.Writer)} owns it, and '{field}:' names a second"));
            }
        }
    }

    private static string? OwnerField(StateWriter writer)
        => writer switch
        {
            StateWriter.Tool => "from",
            StateWriter.Counter => IncrementField,
            StateWriter.Const => "value",
            _ => null,
        };

    private static string WriterName(StateWriter writer)
        => writer switch
        {
            StateWriter.Tool => "tool",
            StateWriter.Counter => "counter",
            StateWriter.Const => "const",
            _ => "extractor",
        };

    private static object? FieldValue(StateSlotConfiguration slot, string field)
        => field switch
        {
            "from" => slot.From,
            IncrementField => slot.Increment,
            _ => slot.Value,
        };
}
