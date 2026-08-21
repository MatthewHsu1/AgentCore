using AgentCore.Application.Configuration.Parsing;

namespace AgentCore.Application.Tools;

/// <summary>
/// The failure a tool source throws when it cannot serve a declaration.
/// </summary>
/// <remarks>
/// Every one of these points at <c>/tools</c> and reports
/// <see cref="ConfigurationCheck.ReferenceResolution"/>. Sources share this so a host reading the
/// error gets the same pointer and the same check whichever source threw it.
/// </remarks>
public static class ToolSourceError
{
    /// <summary>Builds the failure one message describes.</summary>
    /// <param name="message">What is wrong, and what the document should say instead.</param>
    /// <returns>The exception to throw.</returns>
    public static ConfigurationLoadException Fail(string message)
        => new(new ConfigurationError
        {
            Pointer = "/tools",
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });
}
