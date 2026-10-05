using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.State;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>What every session one factory builds shares: the entry, and the seams it runs on.</summary>
    /// <param name="Compiled">The compiled entry every conversation runs.</param>
    /// <param name="Guards">The evaluator that runs each exit guard.</param>
    /// <param name="Extractor">The state extractor the document declares, or <see langword="null"/>.</param>
    /// <param name="Time">The clock every turn reads.</param>
    /// <param name="Logger">Where the session's own log events go, or <see langword="null"/> for none.</param>
    /// <param name="Hooks">The hooks every session of this factory raises its notices to.</param>
    internal sealed record ConversationSessionSeams(
        CompiledAgent Compiled,
        IGuardEvaluator Guards,
        StateExtractor? Extractor,
        TimeProvider Time,
        ILogger? Logger,
        HookRuntime Hooks);
}
