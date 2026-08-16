using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// The one collection every class that touches the disk result store belongs to.
/// </summary>
/// <remarks>
/// <para>
/// A test run gives each class a collection of its own and runs collections in parallel. Every class
/// in this one reads or writes the same store under <see cref="EvalHarness.StorageRoot"/>, and
/// <c>DiskBasedResultStore</c> opens a result file without sharing it: a write creates the file, a
/// read holds it open, and neither lets the other in. Overlap them on one file and the write throws
/// an <see cref="IOException"/>. The suite then goes red for a reason that has nothing to do with a
/// score, and it does so only sometimes, which is the worst way for a gate to fail.
/// </para>
/// <para>
/// Naming this collection puts those classes in one queue, so no two ever hold the same result file
/// at once. It buys nothing else. It fixes no order, and the gate still has to run after the suites
/// that write what it reads. The <c>Category</c> filter of <see cref="BaselineGateTests"/> is what
/// supplies that.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class EvalStoreCollection
{
    /// <summary>The name each class that touches the store carries.</summary>
    public const string Name = "EvalStore";
}
