using System.Globalization;
using AgentCore.Application.Configuration.Parsing;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>Builds a <see cref="ConfigurationError"/> tagged with the check that found it.</summary>
internal static class ValidationErrors
{
    /// <summary>
    /// The longest interval, in seconds, that every timer these values reach will accept.
    /// <c>CancellationTokenSource.CancelAfter</c>, <c>Task.WaitAsync</c> and <c>PeriodicTimer</c> each
    /// throw above <see cref="int.MaxValue"/> milliseconds, from a call site that carries no pointer
    /// into the document.
    /// </summary>
    public const int MaxIntervalSeconds = int.MaxValue / 1000;

    public static ConfigurationError Reference(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.ReferenceResolution };

    public static ConfigurationError Range(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.ValueRange };

    public static ConfigurationError Writers(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.SlotWriters };

    public static ConfigurationError Operators(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.GuardOperators };

    public static ConfigurationError Reachability(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.Reachability };

    public static ConfigurationError WellFormedness(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.GraphWellFormedness };

    /// <summary>Refuses a count or an interval that falls outside the range the runtime accepts.</summary>
    /// <param name="value">The configured value.</param>
    /// <param name="min">The lowest accepted value, inclusive.</param>
    /// <param name="max">The highest accepted value, inclusive. <see cref="int.MaxValue"/> means no ceiling.</param>
    /// <param name="pointer">The pointer at the field itself.</param>
    /// <param name="subject">How the message names the field, as a noun phrase.</param>
    /// <param name="why">One or more sentences saying what the range protects.</param>
    /// <param name="errors">Collects the refusal.</param>
    /// <remarks>
    /// Both bounds go through one call so a field cannot be given a floor and left without a ceiling:
    /// every ceiling here stands between a document and a raw throw out of a timer at boot or mid-turn.
    /// </remarks>
    public static void CheckRange(
        int value,
        int min,
        int max,
        string pointer,
        string subject,
        string why,
        List<ConfigurationError> errors)
    {
        if (value >= min && value <= max)
        {
            return;
        }

        var accepted = max == int.MaxValue
            ? $"the lowest accepted value is {min.ToString(CultureInfo.InvariantCulture)}"
            : $"the accepted range is {min.ToString(CultureInfo.InvariantCulture)} to "
                + max.ToString(CultureInfo.InvariantCulture);

        errors.Add(Range(
            pointer,
            $"{subject} is {value.ToString(CultureInfo.InvariantCulture)}, and {accepted}. {why}"));
    }
}
