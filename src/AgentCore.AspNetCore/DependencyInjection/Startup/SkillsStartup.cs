using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Opens the skills folder the host bound, before the document is compiled.</summary>
internal static class SkillsStartup
{
    /// <summary>MAF's own cap. It is a const on AgentFileSkillsSource, not a settable option.</summary>
    private const int MaximumDepth = 2;

    /// <summary>Builds the catalog the compile table reads.</summary>
    /// <param name="options">The host's options. They carry the folder or the source.</param>
    /// <param name="loggers">Where the source writes its diagnostics.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The catalog, or <see langword="null"/> when the host bound nothing.</returns>
    /// <exception cref="ConfigurationLoadException">The folder is unusable.</exception>
    internal static async ValueTask<SkillCatalog?> OpenAsync(
        AgentCoreOptions options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggers);

        AgentSkillsSource source;
        string? path = null;

        if (options.SkillsSource is { } bound)
        {
            source = bound;
        }
        else if (options.SkillsPath is { } folder)
        {
            path = folder;

            if (!Directory.Exists(path))
            {
                throw Fail(File.Exists(path)
                    ? $"the skills path '{path}' is a file, not a folder, so no skill can load. "
                      + "Pass options.UseSkills(...) the folder that holds the skill directories."
                    : $"the skills folder '{path}' does not exist, so no skill can load. "
                      + "Check the path passed to options.UseSkills(...), and that the folder ships with the app.");
            }

            // Scripts are refused at three layers, and this is the one the model cannot see past: without
            // it, load_skill's own result advertises every script file with its parameter schema.
            AgentFileSkillsSourceOptions fileOptions = new() { ScriptFilter = _ => false };
            source = new CachingAgentSkillsSource(new AgentFileSkillsSource(path, null, fileOptions, loggers));
        }
        else
        {
            return null;
        }

        try
        {
            return new SkillCatalog(source, await NamesAsync(source, path, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            // AgentCoreBoot takes ownership of what this returns, so a check inside NamesAsync that
            // throws leaves nothing else holding the source — a host's own included, since
            // UseSkills(AgentSkillsSource) took ownership of that one.
            source.Dispose();
            throw;
        }
    }

    /// <summary>Enumerates once, then runs every check the framework does not.</summary>
    /// <param name="source">The source to enumerate.</param>
    /// <param name="path">The bound folder, or <see langword="null"/> when the host bound a source.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>Every name the source serves.</returns>
    /// <exception cref="ConfigurationLoadException">The folder is unusable.</exception>
    private static async ValueTask<IReadOnlySet<string>> NamesAsync(
        AgentSkillsSource source,
        string? path,
        CancellationToken cancellationToken)
    {
        IList<AgentSkill> skills;
        List<string> candidates = [];

        try
        {
            using ProbeChatClient client = new();
            ChatClientAgent probe = new(client, new ChatClientAgentOptions { Name = "skills-probe" });

            skills = await source
                .GetSkillsAsync(new AgentSkillsSourceContext(probe, null), cancellationToken)
                .ConfigureAwait(false);

            if (path is not null)
            {
                Walk(path, 0, candidates);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Fail(
                path is null
                    ? $"the skills source bound by options.UseSkills(...) could not be read: {exception.Message} "
                      + "Grant the process access to whatever that source reads."
                    : $"the skills folder '{path}' could not be read: {exception.Message} "
                      + "Grant the process read access to the folder and to every skill directory under it.",
                exception);
        }

        // This check runs first on purpose. A folder whose only skill is dropped enumerates to zero,
        // so a "serves no skill" test placed above would win and hide the directory that is actually
        // at fault — which is the whole reason for reproducing the framework's walk.
        if (path is not null)
        {
            var loaded = skills
                .OfType<AgentFileSkill>()
                .Select(skill => Path.GetFullPath(skill.Path))
                .ToHashSet(StringComparer.Ordinal);

            var dropped = candidates.Where(candidate => !loaded.Contains(candidate)).ToList();
            if (dropped.Count > 0)
            {
                throw Fail($"these directories hold a SKILL.md that did not load: {string.Join(", ", dropped.Select(directory => $"'{directory}'"))}. "
                           + "The usual causes are a frontmatter name: that differs from the directory name, "
                           + "or a directory name outside the skill charset of lower-case letters, digits and "
                           + "single hyphens. The framework logs the reason at Error level.");
            }
        }

        if (skills.Count == 0)
        {
            throw Fail(path is null
                ? "the skills source bound by options.UseSkills(...) serves no skill."
                : $"the skills folder '{path}' serves no skill. Each skill is a directory holding a "
                  + "SKILL.md, one or two levels below the folder.");
        }

        var duplicates = skills
            .GroupBy(skill => skill.Frontmatter.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();

        if (duplicates.Count > 0)
        {
            var detail = string.Join("; ", duplicates.Select(Describe));

            throw Fail(path is null
                ? $"the skills source bound by options.UseSkills(...) serves one name more than once: {detail}. "
                  + "Which copy loads is decided by the order the source returned them, so the answer can "
                  + "change between runs. Serve each name once."
                : $"the skills folder serves one name from more than one directory: {detail}. "
                  + "Which one loads is decided by the order the folder was written to disk, so two "
                  + "deploys of one commit can differ. Rename one.");
        }

        return skills.Select(skill => skill.Frontmatter.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Names the copies of one duplicated skill name.</summary>
    /// <param name="group">Every skill the source served under that name.</param>
    /// <returns>The clause naming the name and its copies.</returns>
    private static string Describe(IGrouping<string, AgentSkill> group)
    {
        // A source the host bound need serve no file skill at all, and a skill with no path leaves
        // nothing to point at but how many copies there are and what they are.
        if (!group.All(skill => skill is AgentFileSkill))
        {
            var types = group
                .Select(skill => skill.GetType().Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            return $"'{group.Key}' is served by {group.Count()} skills, of type {string.Join(" and ", types)}";
        }

        var paths = group.Cast<AgentFileSkill>().Select(skill => $"'{skill.Path}'");

        return $"'{group.Key}' is served by {string.Join(" and ", paths)}";
    }

    /// <summary>
    /// Reproduces the framework's own discovery walk. It is not a glob: a directory holding a
    /// SKILL.md is never descended into, and the bound folder itself is a candidate, so a
    /// <c>*/SKILL.md</c> pattern would report a legal nested layout as a dropped skill.
    /// </summary>
    /// <param name="directory">The directory to test.</param>
    /// <param name="depth">How far below the bound folder this directory sits.</param>
    /// <param name="candidates">The list every candidate directory is added to.</param>
    private static void Walk(string directory, int depth, List<string> candidates)
    {
        if (File.Exists(Path.Combine(directory, "SKILL.md")))
        {
            candidates.Add(Path.GetFullPath(directory));
            return;
        }

        if (depth >= MaximumDepth)
        {
            return;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            Walk(child, depth + 1, candidates);
        }
    }

    /// <summary>Builds the one exception a load failure uses.</summary>
    /// <param name="message">What went wrong, and what to do next.</param>
    /// <param name="innerException">The cause, when one exists.</param>
    /// <returns>The exception to throw.</returns>
    private static ConfigurationLoadException Fail(string message, Exception? innerException = null)
        => new("The configuration document did not load. " + message, innerException);

    /// <summary>
    /// The framework's enumeration context refuses a null agent, and an agent needs a chat client.
    /// Nothing calls the model during enumeration, so both response methods throw rather than
    /// answer.
    /// </summary>
    private sealed class ProbeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
