namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// The one exception every load failure uses.
/// </summary>
/// <remarks>
/// Section 8.7: loading fails fast, and every message carries a JSON Pointer into the document. The
/// exception carries one error for each reason, so a check that finds several may report them all.
/// </remarks>
public sealed class ConfigurationLoadException : Exception
{
    /// <summary>Creates an exception with no error detail.</summary>
    public ConfigurationLoadException()
        : base("The configuration document did not load.") => Errors = [];

    /// <summary>Creates an exception with a plain message and no error detail.</summary>
    /// <param name="message">The message a human reads.</param>
    public ConfigurationLoadException(string message)
        : base(message) => Errors = [];

    /// <summary>Creates an exception with a plain message and an inner cause.</summary>
    /// <param name="message">The message a human reads.</param>
    /// <param name="innerException">The cause.</param>
    public ConfigurationLoadException(string message, Exception? innerException)
        : base(message, innerException) => Errors = [];

    /// <summary>Creates an exception for one error.</summary>
    /// <param name="error">The reason the document did not load.</param>
    public ConfigurationLoadException(ConfigurationError error)
        : this([error], null)
    {
    }

    /// <summary>Creates an exception for one error, with the cause that produced it.</summary>
    /// <param name="error">The reason the document did not load.</param>
    /// <param name="innerException">The cause, when one exists.</param>
    public ConfigurationLoadException(ConfigurationError error, Exception? innerException)
        : this([error], innerException)
    {
    }

    /// <summary>Creates an exception for several errors.</summary>
    /// <param name="errors">The reasons the document did not load. At least one is expected.</param>
    public ConfigurationLoadException(IEnumerable<ConfigurationError> errors)
        : this(errors, null)
    {
    }

    /// <summary>Creates an exception for several errors, with the cause that produced them.</summary>
    /// <param name="errors">The reasons the document did not load. At least one is expected.</param>
    /// <param name="innerException">The cause, when one exists.</param>
    public ConfigurationLoadException(IEnumerable<ConfigurationError> errors, Exception? innerException)
        : base(BuildMessage(errors), innerException)
        => Errors = [.. errors];

    /// <summary>Gets every reason the document did not load, in the order the check found them.</summary>
    public IReadOnlyList<ConfigurationError> Errors { get; }

    /// <summary>Gets the JSON Pointer of the first error, or the root pointer when there is none.</summary>
    public string Pointer => Errors.Count > 0 ? Errors[0].Pointer : ConfigurationError.RootPointer;

    /// <summary>Gets the check that produced the first error.</summary>
    public ConfigurationCheck Check => Errors.Count > 0 ? Errors[0].Check : ConfigurationCheck.Syntax;

    private static string BuildMessage(IEnumerable<ConfigurationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var lines = errors.Select(error => error.ToString()).ToList();
        return lines.Count switch
        {
            0 => "The configuration document did not load.",
            1 => "The configuration document did not load. " + lines[0],
            _ => "The configuration document did not load. " + lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + " errors:" + Environment.NewLine + string.Join(Environment.NewLine, lines),
        };
    }
}
