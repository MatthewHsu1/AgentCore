using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// The collection of every class that writes results to the disk store.
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
/// at once. It buys nothing else. It fixes no order, and the classes in it may run in any order among
/// themselves, which is fine because none of them reads what another wrote.
/// </para>
/// <para>
/// <see cref="BaselineGateTests"/> does read what they wrote, and that is why it is not in here. It
/// sits alone in <see cref="EvalGateCollection"/> so that <see cref="GateLastCollectionOrderer"/> can
/// put it after this one: an orderer sorts collections, so a class that needs to run last needs a
/// collection to itself.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class EvalStoreCollection
{
    /// <summary>The name each class that writes to the store carries.</summary>
    public const string Name = "EvalStore";
}
