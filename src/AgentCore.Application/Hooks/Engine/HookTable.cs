using System.Reflection;

namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>
    /// What each registered hook overrides, found once by reflection. A method no hook overrides
    /// has an empty array here, so its gate or notice costs one length check.
    /// </summary>
    internal sealed class HookTable
    {
        private static readonly MethodInfo[] NoticeMethods = [.. typeof(AgentHook)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => method.IsVirtual
                && method.GetParameters() is [{ ParameterType: var type }, _]
                && typeof(HookNotice).IsAssignableFrom(type))];

        private static readonly IReadOnlySet<Type> NoNotices = new HashSet<Type>();

        private readonly Dictionary<GatePoint, AgentHook[]> _gates;

        private readonly Dictionary<AgentHook, IReadOnlySet<Type>> _notices;

        private readonly HashSet<Type> _wanted;

        private HookTable(IReadOnlyList<AgentHook> hooks)
        {
            Hooks = hooks;
            _gates = GatePoint.All.ToDictionary(
                static point => point,
                point => hooks.Where(hook => Overrides(hook, typeof(AgentHook).GetMethod(point.MethodName)!)).ToArray());
            _notices = new Dictionary<AgentHook, IReadOnlySet<Type>>(ReferenceEqualityComparer.Instance);

            foreach (AgentHook hook in hooks)
            {
                HashSet<Type> types = [.. NoticeMethods
                    .Where(method => Overrides(hook, method))
                    .Select(static method => method.GetParameters()[0].ParameterType)];

                if (types.Count > 0)
                {
                    _notices[hook] = types;
                }
            }

            NoticeHooks = [.. hooks.Where(_notices.ContainsKey)];
            _wanted = [.. _notices.Values.SelectMany(static types => types)];
        }

        /// <summary>Gets a table with no hooks.</summary>
        internal static HookTable Empty { get; } = new([]);

        /// <summary>Gets every hook, in the order they run: AgentCore's built-ins first, then the host's.</summary>
        internal IReadOnlyList<AgentHook> Hooks { get; }

        /// <summary>Gets the hooks that override at least one notice method, in run order.</summary>
        internal IReadOnlyList<AgentHook> NoticeHooks { get; }

        /// <summary>Builds the table. An instance listed twice counts once, at its first place.</summary>
        /// <param name="hooks">The hooks in run order.</param>
        /// <returns>The table.</returns>
        internal static HookTable Build(IReadOnlyList<AgentHook> hooks)
        {
            ArgumentNullException.ThrowIfNull(hooks);

            List<AgentHook> distinct = [];
            HashSet<AgentHook> seen = new(ReferenceEqualityComparer.Instance);
            foreach (AgentHook hook in hooks)
            {
                if (hook is null)
                {
                    throw new ArgumentNullException(nameof(hooks), "A hook in the list is null.");
                }

                if (seen.Add(hook))
                {
                    distinct.Add(hook);
                }
            }

            return new HookTable(distinct);
        }

        internal AgentHook[] For(GatePoint point)
        {
            return _gates[point];
        }

        internal bool Overrides(GatePoint point)
        {
            return _gates[point].Length > 0;
        }

        internal IReadOnlySet<Type> NoticesOf(AgentHook hook)
        {
            return _notices.TryGetValue(hook, out IReadOnlySet<Type>? types) ? types : NoNotices;
        }

        internal bool Wants(Type noticeType)
        {
            return _wanted.Contains(noticeType);
        }

        internal bool OverridesAnyGate(AgentHook hook)
        {
            return GatePoint.All.Any(point => Array.Exists(_gates[point], listed => ReferenceEquals(listed, hook)));
        }

        /// <summary>Whether a hook type overrides one gate, read from the type alone.</summary>
        /// <param name="hookType">The hook's type.</param>
        /// <param name="point">The gate.</param>
        /// <returns><see langword="true"/> when the type overrides the gate's method.</returns>
        internal static bool TypeOverrides(Type hookType, GatePoint point)
        {
            return TypeOverrides(hookType, typeof(AgentHook).GetMethod(point.MethodName)!);
        }

        private static bool Overrides(AgentHook hook, MethodInfo baseMethod)
        {
            return TypeOverrides(hook.GetType(), baseMethod);
        }

        // GetBaseDefinition tells an override apart from a method hidden with `new`, which the base dispatch never calls.
        private static bool TypeOverrides(Type hookType, MethodInfo baseMethod)
        {
            MethodInfo? found = hookType.GetMethod(
                baseMethod.Name,
                BindingFlags.Public | BindingFlags.Instance,
                [.. baseMethod.GetParameters().Select(static parameter => parameter.ParameterType)]);

            return found is not null
                && found.DeclaringType != typeof(AgentHook)
                && found.GetBaseDefinition() == baseMethod;
        }
    }
}
