using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Registry
{
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

            foreach (IToolSource? source in sources)
            {
                if (source is null)
                {
                    throw new ArgumentNullException(nameof(sources));
                }

                IReadOnlyList<ToolRegistration> registrations = await source.ProvideAsync(context, cancellationToken).ConfigureAwait(false);
                foreach (ToolRegistration registration in registrations)
                {
                    Add(tools, registration, context);
                }
            }

            VerifyEveryDeclarationIsServed(tools, context.Configuration);

            return new ToolRegistry(tools);
        }

        private static void Add(Dictionary<string, Lazy<AITool>> tools, ToolRegistration registration, ToolSourceContext context)
        {
            if (string.IsNullOrWhiteSpace(registration.Description))
            {
                throw ToolSourceError.Fail(
                    $"the tool '{registration.Id}' has no description, so the model has nothing to read when it "
                    + "decides whether to call it. Write a description: on the declaration.");
            }

            // Every resolve runs on the single startup flow that compiles the document. Nothing resolves
            // once the host is serving, so no request thread can race the Lazy.
            Lazy<AITool> lazy = new(() => Cache(Limit(registration), registration.Id, context), LazyThreadSafetyMode.None);

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
            AITool tool = registration.Materialise();

            if (registration.CallTimeout is not { } limit)
            {
                return tool;
            }

            // A source that asked for a deadline and quietly did not get one is the worst of both: the
            // document says the conversation is bounded and nothing bounds it. Only an AIFunction has a conversation to
            // put a deadline around, so a source that names one for anything else is wrong about its own
            // tool, and says so at boot rather than on a live conversation.
            return tool is AIFunction function
                ? new TimeLimitedTool(function, limit)
                : throw ToolSourceError.Fail(
                    $"the tool '{registration.Id}' declares a call timeout, but the source built a "
                    + $"{tool.GetType().Name} rather than an AIFunction, which has no call to time. Take "
                    + "the timeout off the registration, or serve the tool as an AIFunction.");
        }

        /// <summary>Wraps one tool in the cache when its declaration asks for one and the host has one.</summary>
        private static AITool Cache(AITool tool, string id, ToolSourceContext context)
        {
            ToolConfiguration? declared = context.Configuration.Tools.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));

            if (declared?.CacheSeconds is not { } seconds || context.Cache is not { } cache)
            {
                return tool;
            }

            return tool is AIFunction function
                ? new CachedTool(function, cache, TimeSpan.FromSeconds(seconds))
                : throw ToolSourceError.Fail(
                    $"the tool '{id}' declares cacheSeconds, but the source built a {tool.GetType().Name} "
                    + "rather than an AIFunction, which has no call to cache. Take cacheSeconds off the "
                    + "declaration, or serve the tool as an AIFunction.");
        }

        private static void VerifyEveryDeclarationIsServed(
            Dictionary<string, Lazy<AITool>> tools, AgentCoreConfiguration configuration)
        {
            foreach (ToolConfiguration declared in configuration.Tools)
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
}
