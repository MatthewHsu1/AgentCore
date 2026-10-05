using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Skills
{
    /// <summary>
    /// The bodies of an agent's <c>pinned:</c> skills, appended to its instructions on every turn.
    /// </summary>
    internal sealed class PinnedSkillsProvider : AIContextProvider
    {
        private const string FrontmatterFence = "---";

        private const string ManifestStart = "\n<available_resources";

        private readonly SkillCatalog _catalog;

        private readonly IReadOnlyList<string> _names;

        private string? _instructions;

        /// <summary>Creates the provider.</summary>
        /// <param name="catalog">The skills the host bound, shared by every agent.</param>
        /// <param name="names">The names this agent pins, in the order their bodies appear.</param>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="names"/> is empty.</exception>
        internal PinnedSkillsProvider(SkillCatalog catalog, IReadOnlyList<string> names)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(names);

            if (names.Count == 0)
            {
                throw new ArgumentException("A pinned skills provider needs at least one skill name.", nameof(names));
            }

            _catalog = catalog;
            _names = names;
        }

        /// <inheritdoc/>
        protected override async ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            string instructions = _instructions ?? await ReadAsync(context, cancellationToken).ConfigureAwait(false);

            if (TurnRegistry.For(context.Session) is { Hooks: { } hooks } turn && hooks.Wants<SkillLoaded>())
            {
                foreach (string name in _names)
                {
                    _ = hooks.Raise(new SkillLoaded(hooks.Scope(turn.TurnIndex, turn.Stage), name, Pinned: true));
                }
            }

            return new AIContext { Instructions = instructions };
        }

        /// <summary>
        /// Reads every pinned body once. Two first turns racing here both read the files and store
        /// the same text, so there is no lock.
        /// </summary>
        private async ValueTask<string> ReadAsync(InvokingContext context, CancellationToken cancellationToken)
        {
            IList<AgentSkill> skills = await _catalog.Source
                .GetSkillsAsync(new AgentSkillsSourceContext(context.Agent, context.Session), cancellationToken)
                .ConfigureAwait(false);

            List<string> bodies = new(_names.Count);
            foreach (string name in _names)
            {
                AgentSkill skill = skills.FirstOrDefault(candidate => string.Equals(candidate.Frontmatter.Name, name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"The pinned skill '{name}' is not in the bound skills folder. Validation should have refused it.");

                string content = await skill.GetContentAsync(cancellationToken).ConfigureAwait(false);
                bodies.Add($"<skill name=\"{name}\">\n{StripManifests(StripFrontmatter(content)).Trim()}\n</skill>");
            }

            _instructions = string.Join("\n\n", bodies);
            return _instructions;
        }

        /// <summary>
        /// Drops the <c>&lt;available_resources&gt;</c> and <c>&lt;available_scripts&gt;</c> blocks MAF
        /// appends to a file skill's content. They list what <c>read_skill_resource</c> can fetch, and a
        /// pinned skill has no such tool, so the list would only tempt the model.
        /// </summary>
        private static string StripManifests(string content)
        {
            int start = content.LastIndexOf(ManifestStart, StringComparison.Ordinal);
            return start < 0 ? content : content[..start];
        }

        /// <summary>
        /// Drops the YAML frontmatter. Its name and description serve skill discovery, which a pinned
        /// skill never goes through, so the model gets the body alone.
        /// </summary>
        private static string StripFrontmatter(string content)
        {
            if (!content.StartsWith(FrontmatterFence, StringComparison.Ordinal))
            {
                return content;
            }

            int close = content.IndexOf("\n" + FrontmatterFence, FrontmatterFence.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                return content;
            }

            int bodyStart = content.IndexOf('\n', close + 1);
            return bodyStart < 0 ? string.Empty : content[(bodyStart + 1)..];
        }
    }
}
