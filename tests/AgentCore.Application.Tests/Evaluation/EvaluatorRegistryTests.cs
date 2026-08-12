using AgentCore.Application.Evaluation;
using AgentCore.Application.Tests.Evaluation.Fakes;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace AgentCore.Application.Tests.Evaluation;

/// <summary>
/// The registry decision D13 asks for: names map to <see cref="IEvaluator"/> instances.
/// </summary>
/// <remarks>
/// The registry takes the shape <c>ToolBindingRegistry</c> takes, so these tests read the same as
/// the binding tests do. The registry holds evaluators and never runs one.
/// </remarks>
public sealed class EvaluatorRegistryTests
{
    [Fact]
    public void TheRegistry_HoldsWhatTheHostRegisters()
    {
        EvaluatorRegistry registry = new();
        registry.Register("fault_code", new FaultCodeEvaluator());

        Assert.Equal(1, registry.Count);
        Assert.True(registry.Contains("fault_code"));
        Assert.Equal(["fault_code"], registry.Names);
    }

    [Fact]
    public void ANameIsOrdinal()
    {
        EvaluatorRegistry registry = new();
        registry.Register("fault_code", new FaultCodeEvaluator());

        Assert.False(registry.Contains("Fault_Code"));
    }

    [Fact]
    public void TheSameNameTwice_FailsAtStartup()
    {
        EvaluatorRegistry registry = new();
        registry.Register("fault_code", new FaultCodeEvaluator());

        Assert.Throws<ArgumentException>(() => registry.Register("fault_code", new FaultCodeEvaluator()));
    }

    [Fact]
    public void RegistrationChains()
    {
        EvaluatorRegistry registry = new EvaluatorRegistry()
            .Register("fault_code", new FaultCodeEvaluator())
            .Register("recorded", new RecordingEvaluator());

        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void TheRegistryReadsBackTheSameInstance()
    {
        FaultCodeEvaluator evaluator = new();
        EvaluatorRegistry registry = new();
        registry.Register("fault_code", evaluator);

        Assert.True(registry.TryGetEvaluator("fault_code", out IEvaluator? read));
        Assert.Same(evaluator, read);
    }

    [Fact]
    public void AnUnknownName_ReadsNothing()
    {
        EvaluatorRegistry registry = new();

        Assert.False(registry.TryGetEvaluator("fault_code", out IEvaluator? read));
        Assert.Null(read);
    }

    [Fact]
    public void AnEmptyNameOrANullEvaluator_FailsAtStartup()
    {
        EvaluatorRegistry registry = new();

        Assert.Throws<ArgumentException>(() => registry.Register(string.Empty, new FaultCodeEvaluator()));
        Assert.Throws<ArgumentNullException>(() => registry.Register("fault_code", null!));
    }
}
