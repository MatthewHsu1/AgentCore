using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// Wraps one per-declaration tool builder in the <see cref="ToolRegistry"/> a compiled document
/// reads.
/// </summary>
/// <remarks>
/// The builder is synchronous by contract — a test fake's own <c>Create</c> method, usually passed
/// as a method group — so <see cref="Source.ProvideAsync"/> always returns an already-completed
/// <see cref="ValueTask"/> and <see cref="ToolRegistryBuilder.BuildAsync"/>'s state machine runs to
/// completion synchronously. Blocking on the result here never risks a deadlock.
/// </remarks>
public static class TestToolRegistry
{
    /// <summary>Builds the registry one builder serves, or <see langword="null"/> when the test passed none.</summary>
    /// <param name="document">The document the builder resolves declarations against.</param>
    /// <param name="builder">
    /// Builds the tool one declaration names, or <see langword="null"/> when it does not serve that
    /// declaration's kind. Pass <see langword="null"/> for a test that needs no tools at all.
    /// </param>
    /// <param name="cancellationToken">Cancels the build.</param>
    public static ToolRegistry? From(
        AgentCoreConfiguration document,
        Func<ToolConfiguration, AITool?>? builder,
        CancellationToken cancellationToken)
        => builder is null
            ? null
            : ToolRegistryBuilder.BuildAsync(
                [new Source(builder)], new ToolSourceContext(document), cancellationToken)
                .AsTask().GetAwaiter().GetResult();

    private sealed class Source(Func<ToolConfiguration, AITool?> builder) : IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            List<ToolRegistration> registrations = [];
            foreach (var declared in context.Configuration.Tools)
            {
                if (builder(declared) is not { } built)
                {
                    continue;
                }

                registrations.Add(new ToolRegistration(
                    declared.Id, declared.Description ?? string.Empty, () => built));
            }

            return ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(registrations);
        }
    }
}
