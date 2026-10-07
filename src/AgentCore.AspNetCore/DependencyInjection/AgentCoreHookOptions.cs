using AgentCore.Application.Hooks;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.DependencyInjection
{
    /// <summary>Registers the host's hooks. AgentCore's own built-in hooks always run first.</summary>
    public static class AgentCoreHookOptions
    {
        /// <summary>
        /// Adds a hook resolved once from the container: the registered singleton when there is one, else an
        /// instance built with constructor injection. One instance serves every conversation.
        /// </summary>
        /// <typeparam name="THook">The hook type.</typeparam>
        /// <param name="options">The options.</param>
        /// <returns>The options, so a host chains its calls.</returns>
        public static AgentCoreOptions UseHooks<THook>(this AgentCoreOptions options)
            where THook : AgentHook
        {
            ArgumentNullException.ThrowIfNull(options);

            options.HookFactories.Add((typeof(THook), static services => services is null
                ? throw new InvalidOperationException($"UseHooks<{typeof(THook).Name}>() needs the host's container, and this boot has none.")
                : ActivatorUtilities.GetServiceOrCreateInstance<THook>(services)));
            return options;
        }

        /// <summary>Adds hook instances, in order. Calling it again adds more.</summary>
        /// <param name="options">The options.</param>
        /// <param name="hooks">The hooks. One instance serves every conversation.</param>
        /// <returns>The options, so a host chains its calls.</returns>
        public static AgentCoreOptions UseHooks(this AgentCoreOptions options, params AgentHook[] hooks)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(hooks);

            foreach (AgentHook hook in hooks)
            {
                if (hook is null)
                {
                    throw new ArgumentNullException(nameof(hooks), "A hook in the list is null.");
                }

                options.HookFactories.Add((hook.GetType(), _ => hook));
            }

            return options;
        }
    }
}
