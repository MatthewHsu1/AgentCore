using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// The collection of the one class that only reads the disk result store.
/// </summary>
/// <remarks>
/// <para>
/// The gate measures results it did not produce. Everything it reads is written by the suites in
/// <see cref="EvalStoreCollection"/>, so it is the last thing in the assembly that may run. A run that
/// starts it first measures an empty directory and fails on a missing result rather than on a score.
/// </para>
/// <para>
/// A collection is the only unit the runner will order, so the gate needs one of its own. It cannot
/// share <see cref="EvalStoreCollection"/> and still be ordered, because
/// <see cref="Xunit.v3.ITestCollectionOrderer"/> sorts collections and never the classes inside one.
/// That split is the whole reason this type exists. <see cref="GateLastCollectionOrderer"/> is what
/// then puts it last.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class EvalGateCollection
{
    /// <summary>The name the gate carries.</summary>
    public const string Name = "EvalGate";
}
