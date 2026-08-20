using AgentCore.Evals;
using Xunit;

// The gate reads what the eval suites write, so the run has to finish the writers before it starts
// the reader. These two attributes are what makes that true of a plain `dotnet test`, with no filter
// and no second command.

// An order over collections is only an order if two of them never run at once. This assembly runs in
// about 300 ms, so serialising it costs nothing worth measuring, and it is what turns the sort below
// from a hint into a guarantee.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// The sort itself: EvalGateCollection last, every other collection ahead of it, in discovery order.
[assembly: TestCollectionOrderer(typeof(GateLastCollectionOrderer))]
