using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;

namespace AgentCore.Application.State
{
    /// <summary>
    /// The reminder that an unfilled slot puts in front of the reply agent's input.
    /// </summary>
    public static class UnfilledSlotReminder
    {
        /// <summary>The tag that opens the reminder.</summary>
        public const string OpenTag = "<system-reminder>";

        /// <summary>The tag that closes the reminder.</summary>
        public const string CloseTag = "</system-reminder>";

        private const string Lead = "You still need this from the caller before you can continue: ";

        private const string Tail = "Ask for it in your next reply.";

        /// <summary>
        /// Lists the slots the current stage waits on and that still read as null.
        /// </summary>
        /// <param name="state">The state of one conversation.</param>
        /// <param name="stage">The stage the machine holds.</param>
        /// <returns>The unfilled slot names, in document order.</returns>
        public static IReadOnlyList<string> UnfilledSlots(StateDocument state, StageConfiguration stage)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(stage);

            HashSet<string> read = new(StringComparer.Ordinal);
            CollectStageExits(state.Configuration, stage, read);

            List<string> unfilled = [];
            foreach ((string? name, StateSlotConfiguration? slot) in state.Configuration.State)
            {
                // Only an extractor slot waits on the caller. Every other writer fills itself.
                if (slot.Writer == StateWriter.Extractor && read.Contains(name) && state.Read(name) is null)
                {
                    unfilled.Add(name);
                }
            }

            return unfilled;
        }

        /// <summary>Builds the reminder for a set of unfilled slots.</summary>
        /// <param name="configuration">The loaded document, which holds each slot description.</param>
        /// <param name="slots">The unfilled slot names.</param>
        /// <returns>The reminder text, or <see langword="null"/> when no slot is unfilled.</returns>
        public static string? Build(AgentCoreConfiguration configuration, IReadOnlyList<string> slots)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(slots);

            if (slots.Count == 0)
            {
                return null;
            }

            List<string> wanted = [];
            foreach (string slot in slots)
            {
                wanted.Add(configuration.State.TryGetValue(slot, out StateSlotConfiguration? declared) && declared.Description is { Length: > 0 } text
                    ? text
                    : slot);
            }

            StringBuilder builder = new();
            _ = builder.Append(OpenTag).Append('\n');
            _ = builder.Append(Lead).Append(Join(wanted)).Append(".\n");
            _ = builder.Append(Tail).Append('\n');
            _ = builder.Append(CloseTag);
            return builder.ToString();
        }

        /// <summary>Builds the reminder for the stage the machine holds.</summary>
        /// <param name="state">The state of one conversation.</param>
        /// <param name="stage">The stage the machine holds.</param>
        /// <returns>The reminder text, or <see langword="null"/> when no slot is unfilled.</returns>
        public static string? Build(StateDocument state, StageConfiguration stage)
        {
            ArgumentNullException.ThrowIfNull(state);
            return Build(state.Configuration, UnfilledSlots(state, stage));
        }

        private static string Join(List<string> wanted)
        {
            return wanted.Count switch
            {
                1 => wanted[0],
                2 => wanted[0] + " and " + wanted[1],
                _ => string.Join(", ", wanted.Take(wanted.Count - 1)) + ", and " + wanted[^1],
            };
        }

        /// <summary>Adds every slot the exits of one stage read to a set.</summary>
        /// <param name="configuration">The loaded document, which holds the named guards.</param>
        /// <param name="stage">The stage.</param>
        /// <param name="names">The set to fill.</param>
        /// <remarks>
        /// A stage waits on the slots its exit guards read, and a guard reads a slot through <c>var</c>
        /// and through <c>missing</c> alike. <see cref="GuardRuleFacts"/> already walks both forms for
        /// checks 4 and 5, so this reuses it rather than open-coding a second, narrower walk that only
        /// catches <c>var</c>. Section 8.4 bans the iteration operators, so that walk is finite.
        /// </remarks>
        private static void CollectStageExits(AgentCoreConfiguration configuration, StageConfiguration stage, HashSet<string> names)
        {
            foreach (GuardReference? when in stage.To.Select(exit => exit.When))
            {
                if (when is null)
                {
                    // An unconditional exit waits on no slots, so it adds nothing to read.
                    continue;
                }

                JsonNode? rule = when.Rule;
                if (rule is null
                    && when.Name is { } guardName
                    && configuration.Guards.TryGetValue(guardName, out JsonNode? named))
                {
                    rule = named;
                }

                GuardRuleFacts facts = new();
                facts.Collect(rule);
                foreach (string name in facts.Variables)
                {
                    _ = names.Add(name);
                }
            }
        }
    }
}
