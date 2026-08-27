using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools.Drawing;
using AgentCore.Application.Tools.Registry;
using AgentCore.Application.Tools.Shipped;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>
/// Serves the <c>kind: builtin</c> tools.
/// </summary>
public sealed class BuiltinToolSource : IToolSource
{
    private static readonly Dictionary<string, IBuiltinToolDefinition> Definitions =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IShippedAgentDefinition> ShippedAgents =
        new IShippedAgentDefinition[]
        {
            new DrawingAgentDefinition(),
        }.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

    private readonly BuiltinToolPorts _ports;

    /// <summary>Creates the source.</summary>
    /// <param name="ports">The adapters the host bound.</param>
    public BuiltinToolSource(BuiltinToolPorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        _ports = ports;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
        ToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<ToolRegistration> registrations = [];

        foreach (var declared in context.DeclarationsOf(ToolKind.Builtin))
        {
            // Built eagerly, whichever table serves it: a definition reports an unbound port by
            // throwing, and that failure belongs on the boot rather than on the first call.
            var uses = declared.Uses;

            if (uses is not null && ShippedAgents.TryGetValue(uses, out var shipped))
            {
                var describedAgent = Described(declared, shipped);
                var agent = ShippedAgentBuilder.Build(shipped, describedAgent, _ports);

                registrations.Add(new ToolRegistration(declared.Id, describedAgent.Description!, () => agent));
                continue;
            }

            if (uses is null || !Definitions.TryGetValue(uses, out var definition))
            {
                throw UnknownName(declared);
            }

            if (UnreadDial(declared) is { } dial)
            {
                throw ToolSourceError.Fail(
                    $"the tool '{declared.Id}' is kind: builtin and uses: '{uses}', which is a plain function "
                    + $"and reads no {dial}. Take the key out, or point uses: at a shipped agent "
                    + $"({string.Join(", ", ShippedAgents.Keys)}).");
            }

            var resolved = Described(declared, definition);
            var built = definition.Build(resolved, _ports);

            registrations.Add(new ToolRegistration(declared.Id, resolved.Description!, () => built));
        }

        return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(registrations);
    }

    /// <summary>
    /// Applies the one description rule: what the document wrote, else what the source ships.
    /// </summary>
    /// <param name="declared">The declaration the document holds.</param>
    /// <param name="definition">The shipped thing that name serves.</param>
    /// <returns>The declaration with a description on it.</returns>
    internal static ToolConfiguration Described(ToolConfiguration declared, IToolDefinition definition)
        => declared.Description is null ? declared with { Description = definition.DefaultDescription } : declared;

    internal static ConfigurationLoadException Unbound(ToolConfiguration tool, string uses, string port)
        => ToolSourceError.Fail(
            $"the tool '{tool.Id}' is kind: builtin and uses: '{uses}', which reads {port}, and no "
            + "adapter binds that port. Bind one, or take the tool out of the document.");

    /// <summary>Names a dial the document set on a built-in that has nothing to spend it on.</summary>
    /// <param name="tool">The declaration the document holds.</param>
    /// <returns>The key, or <see langword="null"/> when the declaration sets neither.</returns>
    private static string? UnreadDial(ToolConfiguration tool)
        => tool.Model is not null ? "model:" : tool.MaxRounds is not null ? "maxRounds:" : null;

    private static ConfigurationLoadException UnknownName(ToolConfiguration tool)
        => ToolSourceError.Fail(
            $"the tool '{tool.Id}' is kind: builtin and uses: '{tool.Uses}', which AgentCore does "
            + $"not ship. This release ships {string.Join(", ", Definitions.Keys.Concat(ShippedAgents.Keys))}.");
}
