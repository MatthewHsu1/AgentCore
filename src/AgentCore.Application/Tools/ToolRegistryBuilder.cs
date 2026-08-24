using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// Asks every source what it serves, and holds the boot rules the answers must pass.
/// </summary>
public static class ToolRegistryBuilder
{
    /// <summary>Builds the one registry the compile table reads.</summary>
    /// <param name="sources">The sources, asked in order.</param>
    /// <param name="context">The document, and what a source resolves against.</param>
    /// <param name="cancellationToken">Cancels the discovery.</param>
    /// <returns>The registry.</returns>
    /// <exception cref="ConfigurationLoadException">
    /// Two sources claim one id, the document declares a tool no source serves, or a tool's
    /// description resolves to empty.
    /// </exception>
    public static async ValueTask<ToolRegistry> BuildAsync(
        IEnumerable<IToolSource> sources,
        ToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(context);

        Dictionary<string, Lazy<AITool>> tools = new(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source, nameof(sources));

            var registrations = await source.ProvideAsync(context, cancellationToken).ConfigureAwait(false);
            foreach (var registration in registrations)
            {
                Add(tools, registration);
            }
        }

        VerifyEveryDeclarationIsServed(tools, context.Configuration);

        return new ToolRegistry(tools);
    }

    private static void Add(Dictionary<string, Lazy<AITool>> tools, ToolRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.Description))
        {
            throw ToolSourceError.Fail(
                $"the tool '{registration.Id}' has no description, so the model has nothing to read when it "
                + "decides whether to call it. Write a description: on the declaration.");
        }

        // Every resolve runs on the single startup flow that compiles the document. Nothing resolves
        // once the host is serving, so no request thread can race the Lazy.
        Lazy<AITool> lazy = new(() => Limit(registration), LazyThreadSafetyMode.None);

        if (!tools.TryAdd(registration.Id, lazy))
        {
            throw ToolSourceError.Fail(
                $"two tools claim the id '{registration.Id}'. An id names one tool, so rename one of "
                + "them or take one out of the document.");
        }
    }

    /// <summary>Builds one tool, with its deadline on it when the source asked for one.</summary>
    private static AITool Limit(ToolRegistration registration)
    {
        var tool = registration.Materialise();

        if (registration.CallTimeout is not { } limit)
        {
            return tool;
        }

        // A source that asked for a deadline and quietly did not get one is the worst of both: the
        // document says the call is bounded and nothing bounds it. Only an AIFunction has a call to
        // put a deadline around, so a source that names one for anything else is wrong about its own
        // tool, and says so at boot rather than on a live call.
        return tool is AIFunction function
            ? new TimeLimitedTool(function, limit)
            : throw ToolSourceError.Fail(
                $"the tool '{registration.Id}' declares a call timeout, but the source built a "
                + $"{tool.GetType().Name} rather than an AIFunction, which has no call to time. Take "
                + "the timeout off the registration, or serve the tool as an AIFunction.");
    }

    private static void VerifyEveryDeclarationIsServed(
        Dictionary<string, Lazy<AITool>> tools, AgentCoreConfiguration configuration)
    {
        foreach (var declared in configuration.Tools)
        {
            // A kind: agent tool reaches no source. The compile table builds it, because it needs
            // the inner agent that only exists once that agent has compiled.
            if (declared.Kind == ToolKind.Agent || tools.ContainsKey(declared.Id))
            {
                continue;
            }

            throw ToolSourceError.Fail(
                $"the tool '{declared.Id}' is kind: {declared.Kind.ToString().ToLowerInvariant()}, and no tool "
                + "source serves it. Register a source for that kind before the document compiles.");
        }
    }
}
