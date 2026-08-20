using System.Globalization;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.State;

/// <summary>
/// Coerces a written value to the declared type of a state slot.
/// </summary>
/// <remarks>
/// Section 8.3: a <c>writer: tool</c> path reads an untyped tool result, so the declared type of the
/// slot is authoritative. AgentCore coerces the value to that type, and leaves the slot at its
/// default when coercion fails.
/// </remarks>
public static class StateValueCoercion
{
    /// <summary>Coerces one value to the declared type of a slot.</summary>
    /// <param name="value">The raw value a writer produced. <see langword="null"/> never coerces.</param>
    /// <param name="slotType">The declared type of the slot.</param>
    /// <param name="coerced">The coerced value, when the coercion succeeds.</param>
    /// <returns><see langword="true"/> when the value coerces to the declared type.</returns>
    public static bool TryCoerce(JsonNode? value, StateSlotType slotType, out JsonNode? coerced)
    {
        coerced = null;

        if (value is not JsonValue scalar)
        {
            // An array or an object is never a scalar slot value, and neither is null.
            return false;
        }

        return slotType switch
        {
            StateSlotType.Boolean => TryCoerceBoolean(scalar, out coerced),
            StateSlotType.Integer => TryCoerceInteger(scalar, out coerced),
            StateSlotType.Number => TryCoerceNumber(scalar, out coerced),
            StateSlotType.String => TryCoerceText(scalar, out coerced),
            _ => false,
        };
    }

    private static bool TryCoerceBoolean(JsonValue scalar, out JsonNode? coerced)
    {
        coerced = null;

        if (scalar.TryGetValue(out bool flag))
        {
            coerced = JsonValue.Create(flag);
            return true;
        }

        if (scalar.TryGetValue(out string? text) && bool.TryParse(text, out var parsed))
        {
            coerced = JsonValue.Create(parsed);
            return true;
        }

        return false;
    }

    private static bool TryCoerceInteger(JsonValue scalar, out JsonNode? coerced)
    {
        coerced = null;

        if (scalar.TryGetValue(out bool _))
        {
            // A boolean is not a number. Section 8.4 rejects the same confusion in a guard.
            return false;
        }

        if (scalar.TryGetValue(out long whole))
        {
            coerced = JsonValue.Create(whole);
            return true;
        }

        if (scalar.TryGetValue(out double real) && double.IsInteger(real))
        {
            coerced = JsonValue.Create((long)real);
            return true;
        }

        if (scalar.TryGetValue(out string? text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            coerced = JsonValue.Create(parsed);
            return true;
        }

        return false;
    }

    private static bool TryCoerceNumber(JsonValue scalar, out JsonNode? coerced)
    {
        coerced = null;

        if (scalar.TryGetValue(out bool _))
        {
            return false;
        }

        if (scalar.TryGetValue(out double real))
        {
            coerced = JsonValue.Create(real);
            return true;
        }

        if (scalar.TryGetValue(out string? text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            coerced = JsonValue.Create(parsed);
            return true;
        }

        return false;
    }

    private static bool TryCoerceText(JsonValue scalar, out JsonNode? coerced)
    {
        coerced = null;

        if (scalar.TryGetValue(out string? text))
        {
            coerced = JsonValue.Create(text);
            return true;
        }

        if (scalar.TryGetValue(out bool flag))
        {
            coerced = JsonValue.Create(flag ? "true" : "false");
            return true;
        }

        if (scalar.TryGetValue(out double real))
        {
            coerced = JsonValue.Create(real.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        return false;
    }
}
