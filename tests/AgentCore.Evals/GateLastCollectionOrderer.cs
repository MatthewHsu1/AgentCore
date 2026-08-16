using Xunit.Sdk;
using Xunit.v3;

namespace AgentCore.Evals;

/// <summary>
/// Runs <see cref="EvalGateCollection"/> after every other collection in this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The gate reads the scores the eval suites write, so it has to run after them. Until this type
/// existed the only thing supplying that order was a CI convention: two commands, one filtered to
/// <c>Category!=Gate</c> and one to <c>Category=Gate</c>. A person who clones the repository and runs
/// <c>dotnet test</c> once runs neither, measures an empty store, and sees a red gate that says
/// nothing about the model. This puts the rule in the assembly instead of in a command someone has to
/// remember, so the plain run is the one that behaves.
/// </para>
/// <para>
/// The sort reads <see cref="ITestCollectionMetadata.TestCollectionClassName"/> and not the display
/// name, because the class name is the thing the compiler can check. Rename
/// <see cref="EvalGateCollection.Name"/> and this still holds; delete the type and this stops
/// building, which is the failure worth having. A collection the runner invented for a class that
/// named none reports an empty class name and sorts with the rest.
/// </para>
/// <para>
/// Everything that is not the gate sorts equal, and <see cref="Enumerable.OrderBy{TSource, TKey}(
/// IEnumerable{TSource}, Func{TSource, TKey})"/> is stable, so the writers keep whatever order
/// discovery gave them. This type fixes one thing and deliberately fixes nothing else.
/// </para>
/// <para>
/// Ordering collections only means something when they run one at a time, which is why the assembly
/// also disables parallelisation. See <c>AssemblyInfo.cs</c>.
/// </para>
/// </remarks>
public sealed class GateLastCollectionOrderer : ITestCollectionOrderer
{
    private static readonly string GateCollection = typeof(EvalGateCollection).FullName!;

    /// <inheritdoc/>
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection
        // A method group will not bind here: TTestCollection is a type parameter, so the compiler
        // wants the conversion to ITestCollection written out. The lambda writes it out.
        => [.. testCollections.OrderBy(collection => RunsLast(collection))];

    /// <summary>Sorts the gate to the back and leaves everything else where it was.</summary>
    private static int RunsLast(ITestCollection collection)
        => string.Equals(collection.TestCollectionClassName, GateCollection, StringComparison.Ordinal) ? 1 : 0;
}
