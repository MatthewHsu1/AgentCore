namespace AgentCore.Application.Secrets;

/// <summary>
/// The one exception a secret reference raises when nothing resolves it.
/// </summary>
/// <remarks>
/// It fails at startup, like every other configuration failure, so a call never discovers a missing
/// credential in the middle of a turn. The message names the secret and the place the document
/// wrote it. It never holds the value of any secret, resolved or not.
/// </remarks>
public sealed class SecretResolutionException : Exception
{
    private const string DefaultMessage = "A ${secret:name} reference did not resolve.";

    /// <summary>Creates an exception that names no secret.</summary>
    public SecretResolutionException()
        : base(DefaultMessage) => SecretName = string.Empty;

    /// <summary>Creates an exception with a plain message.</summary>
    /// <param name="message">The message a human reads. It never holds a secret value.</param>
    public SecretResolutionException(string message)
        : base(message) => SecretName = string.Empty;

    /// <summary>Creates an exception with a plain message and an inner cause.</summary>
    /// <param name="message">The message a human reads. It never holds a secret value.</param>
    /// <param name="innerException">The cause.</param>
    public SecretResolutionException(string message, Exception? innerException)
        : base(message, innerException) => SecretName = string.Empty;

    private SecretResolutionException(string secretName, string message, Exception? innerException)
        : base(message, innerException) => SecretName = secretName;

    /// <summary>Gets the name of the secret that did not resolve, or an empty string.</summary>
    public string SecretName { get; }

    /// <summary>Builds the failure for one name that no resolver holds.</summary>
    /// <param name="secretName">The secret name the document wrote.</param>
    /// <param name="documentPointer">The JSON Pointer to the string that holds the reference, or <see langword="null"/>.</param>
    /// <param name="innerException">The cause, when one exists.</param>
    /// <returns>The failure.</returns>
    public static SecretResolutionException Unresolved(
        string secretName,
        string? documentPointer = null,
        Exception? innerException = null)
    {
        ArgumentNullException.ThrowIfNull(secretName);

        var where = documentPointer is { Length: > 0 } ? $" {documentPointer} reads it." : string.Empty;
        return new SecretResolutionException(
            secretName,
            $"the secret '{secretName}' did not resolve.{where} No resolver in the chain holds that name. "
            + "AgentCore resolves every ${secret:name} reference at startup, so this fails before the "
            + "first call rather than during one.",
            innerException);
    }
}
