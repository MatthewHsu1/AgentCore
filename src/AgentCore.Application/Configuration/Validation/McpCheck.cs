using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>Check 2 over the <c>mcp:</c> list: unique ids, and no secret where it would leak.</summary>
    internal static class McpCheck
    {
        /// <summary>
        /// Refuses a duplicate id. A duplicate connects twice and its collision surfaces only at boot,
        /// naming the served tool id rather than the mcp: entry that caused it. This runs before any
        /// connection opens, so it costs nothing to check here.
        /// </summary>
        public static void ServerIds(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int index = 0; index < configuration.Mcp.Count; index++)
            {
                McpServerConfiguration server = configuration.Mcp[index];
                if (!seen.Add(server.Id))
                {
                    errors.Add(Reference(
                        ConfigurationError.AppendPointer(ValidationPointer.Mcp(index), "id"),
                        $"two mcp: entries declare the id '{server.Id}'. An id names one server, so rename "
                        + "one of them."));
                }
            }
        }

        /// <summary>
        /// Refuses a secret reference in <c>command:</c> or <c>url:</c>. command: becomes the child's
        /// argv, which every user on the box can read out of ps, and url: is logged by proxies and
        /// reverse proxies along the way. Neither is a SecretTemplate, so a reference written there
        /// would be passed through as its literal characters and would leak while not even working.
        /// The schema cannot express "this string may not hold that substring", so it is checked here.
        /// </summary>
        public static void SecretPlacement(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
        {
            for (int index = 0; index < configuration.Mcp.Count; index++)
            {
                McpServerConfiguration server = configuration.Mcp[index];

                for (int word = 0; word < server.Command.Count; word++)
                {
                    if (!server.Command[word].Contains(SecretReference.Prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    errors.Add(Reference(
                        ConfigurationError.AppendPointer(
                            ConfigurationError.AppendPointer(ValidationPointer.Mcp(index), "command"), word),
                        $"the mcp: server '{server.Id}' writes a ${{secret:...}} reference in command:. A "
                        + "command becomes the child process's argv, which every user on this machine can "
                        + "read out of ps, and nothing resolves a reference there — it would be passed "
                        + "through as its own characters. Put the credential in env: instead."));
                }

                if (server.Url is { } url && url.Contains(SecretReference.Prefix, StringComparison.Ordinal))
                {
                    errors.Add(Reference(
                        ConfigurationError.AppendPointer(ValidationPointer.Mcp(index), "url"),
                        $"the mcp: server '{server.Id}' writes a ${{secret:...}} reference in url:. A URL is "
                        + "logged by every proxy it passes, and nothing resolves a reference there — it "
                        + "would be passed through as its own characters. Put the credential in headers: "
                        + "instead."));
                }
            }
        }
    }
}
