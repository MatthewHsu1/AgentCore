using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Skills
{
    /// <summary>
    /// MAF's <c>load_skill</c>, answering a pinned name with where its body already is.
    /// </summary>
    internal sealed class PinnedSkillRedirect : DelegatingAIFunction
    {
        /// <summary>The parameter name of MAF's own <c>load_skill</c> delegate.</summary>
        private const string SkillNameArgument = "skillName";

        private readonly IReadOnlySet<string> _pinned;

        /// <summary>Creates the wrapper.</summary>
        /// <param name="inner">MAF's <c>load_skill</c> tool.</param>
        /// <param name="pinned">The names this agent pins.</param>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        internal PinnedSkillRedirect(AIFunction inner, IReadOnlySet<string> pinned)
            : base(inner)
        {
            ArgumentNullException.ThrowIfNull(pinned);
            _pinned = pinned;
        }

        /// <inheritdoc />
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            if (ReadName(arguments) is { } name && _pinned.Contains(name))
            {
                return ValueTask.FromResult<object?>(
                    $"The '{name}' skill is already in your instructions, inside <skill name=\"{name}\">. Use it from there.");
            }

            return base.InvokeCoreAsync(arguments, cancellationToken);
        }

        private static string? ReadName(AIFunctionArguments arguments)
        {
            _ = arguments.TryGetValue(SkillNameArgument, out object? value);
            return value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            };
        }
    }
}
