using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>
/// Serves the <c>kind: builtin</c> tools.
/// </summary>
public sealed class BuiltinToolSource : IToolSource
{
    private static readonly Dictionary<string, IBuiltinToolDefinition> Definitions =
        new(StringComparer.Ordinal)
        {
            [BuiltinToolNames.KnowledgeSearch] = new KnowledgeSearchDefinition(),
            [BuiltinToolNames.KnowledgeRead] = new KnowledgeReadDefinition(),
            [BuiltinToolNames.KnowledgeList] = new KnowledgeListDefinition(),
            [BuiltinToolNames.KnowledgeGrep] = new KnowledgeGrepDefinition(),
            [BuiltinToolNames.Draw] = new DrawingToolDefinition(),
        };

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
            if (declared.Uses is not { } uses || !Definitions.TryGetValue(uses, out var definition))
            {
                throw UnknownName(declared);
            }

            // Resolved once, so the value the boot validates and the value AIFunctionFactory
            // advertises to the model are the same string.
            var resolved = declared.Description is null
                ? declared with { Description = definition.DefaultDescription }
                : declared;

            // Built eagerly: a definition reports an unbound port by throwing, and that failure
            // belongs on the boot rather than on the first call.
            var built = definition.Build(resolved, _ports);

            registrations.Add(new ToolRegistration(declared.Id, resolved.Description!, () => built));
        }

        return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(registrations);
    }

    internal static ConfigurationLoadException Unbound(ToolConfiguration tool, string uses, string port)
        => ToolSourceError.Fail(
            $"the tool '{tool.Id}' is kind: builtin and uses: '{uses}', which reads {port}, and no "
            + "adapter binds that port. Bind one, or take the tool out of the document.");

    private static ConfigurationLoadException UnknownName(ToolConfiguration tool)
        => ToolSourceError.Fail(
            $"the tool '{tool.Id}' is kind: builtin and uses: '{tool.Uses}', which AgentCore does "
            + $"not ship. This release ships {string.Join(", ", Definitions.Keys)}.");
}
