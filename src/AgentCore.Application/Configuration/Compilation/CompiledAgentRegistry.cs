using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Holds each compiled agent for the life of the process.
/// </summary>
/// <remarks>
/// <para>
/// T44 closed by measurement: a fan-out of 26 simultaneous runs is clean against one shared
/// <c>ChatClientAgent</c>, against one shared <c>AsAIAgent()</c> wrapper, and against one wrapper for
/// each call, with a worst-case first text of 26.5 ms to 27.6 ms across the three shapes. Nothing
/// serializes inside the wrapper, so sharing one agent costs nothing and compiling one for each call
/// buys nothing.
/// </para>
/// <para>
/// Register this registry as a singleton. Every call asks it for the agent, and it compiles once.
/// <see cref="CompileCount"/> exists so a test can prove that.
/// </para>
/// </remarks>
public sealed class CompiledAgentRegistry
{
    private readonly Dictionary<AgentCoreConfiguration, CompiledAgent> _compiled = [];
    private readonly Lock _gate = new();
    private int _compileCount;

    /// <summary>Gets how many times this registry ran the compile table.</summary>
    public int CompileCount
    {
        get
        {
            lock (_gate)
            {
                return _compileCount;
            }
        }
    }

    /// <summary>Gets the compiled agent for one document, compiling it the first time only.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="context">The seams the document names. It is read on the first call only.</param>
    /// <returns>The one compiled agent for that document.</returns>
    public CompiledAgent GetOrCompile(AgentCoreConfiguration configuration, AgentCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            if (_compiled.TryGetValue(configuration, out var existing))
            {
                return existing;
            }

            // The compile runs inside the lock, so 26 simultaneous callers still compile once.
            var compiled = ConfigurationCompiler.Compile(configuration, context);
            _compileCount++;
            _compiled[configuration] = compiled;
            return compiled;
        }
    }
}
