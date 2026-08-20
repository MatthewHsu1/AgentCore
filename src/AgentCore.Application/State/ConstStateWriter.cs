using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.State;

/// <summary>
/// Writer 4 of section 8.3. It fills a slot from a fixed value.
/// </summary>
/// <remarks>
/// A constant never changes, so this writer runs once, when the call starts.
/// </remarks>
public static class ConstStateWriter
{
    /// <summary>Fills every <c>writer: const</c> slot from its <c>value:</c>.</summary>
    /// <param name="state">The state of one call.</param>
    /// <returns>The number of slots this writer filled.</returns>
    public static int Apply(StateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var filled = 0;
        foreach (var (name, slot) in state.Configuration.State)
        {
            if (slot.Writer != StateWriter.Const)
            {
                continue;
            }

            // A value that does not coerce leaves the slot at its default. Section 8.3.
            if (state.TryWrite(name, slot.Value?.DeepClone()))
            {
                filled++;
            }
        }

        return filled;
    }
}
