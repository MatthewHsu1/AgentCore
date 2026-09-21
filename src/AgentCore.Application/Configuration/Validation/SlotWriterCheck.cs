using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>Check 3: one writer for each slot.</summary>
    internal static class SlotWriterCheck
    {
        /// <summary>The <c>increment:</c> field of a counter slot. Named once, so the check and the two maps under it cannot drift apart.</summary>
        private const string IncrementField = "increment";

        public static void Run(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
        {
            foreach (KeyValuePair<string, StateSlotConfiguration> entry in configuration.State)
            {
                string pointer = ValidationPointer.State(entry.Key);
                StateSlotConfiguration slot = entry.Value;

                if (ReservedStateSlots.Contains(entry.Key))
                {
                    errors.Add(Writers(
                        pointer,
                        $"the slot has two writers: '{entry.Key}' is a reserved read-only slot that is always present, and state: declares it again"));
                }

                string? owner = OwnerField(slot.Writer);
                if (owner is not null && FieldValue(slot, owner) is null)
                {
                    errors.Add(Writers(
                        pointer,
                        $"the slot has zero writers: writer: {WriterName(slot.Writer)} fills the slot from '{owner}:', and the slot declares none"));
                }

                foreach (string? field in new[] { "from", IncrementField, "value" })
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
        {
            return writer switch
            {
                StateWriter.Tool => "from",
                StateWriter.Counter => IncrementField,
                StateWriter.Const => "value",
                StateWriter.Extractor => null,
                _ => throw new ArgumentOutOfRangeException(nameof(writer), writer, "The state writer vocabulary is closed, and this value is not in it."),
            };
        }

        private static string WriterName(StateWriter writer)
        {
            return writer switch
            {
                StateWriter.Tool => "tool",
                StateWriter.Counter => "counter",
                StateWriter.Const => "const",
                StateWriter.Extractor => "extractor",
                _ => throw new ArgumentOutOfRangeException(nameof(writer), writer, "The state writer vocabulary is closed, and this value is not in it."),
            };
        }

        private static object? FieldValue(StateSlotConfiguration slot, string field)
        {
            return field switch
            {
                "from" => slot.From,
                IncrementField => slot.Increment,
                _ => slot.Value,
            };
        }
    }
}
