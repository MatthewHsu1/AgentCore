using System.Globalization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// A temp folder of SKILL.md directories, deleted when the test finishes. The framework's
/// discovery walk stops at the first SKILL.md on a branch and gives up past two levels, so a
/// fixture normally puts each skill exactly one level below the root.
/// </summary>
public sealed class SkillFolder : IDisposable
{
    private SkillFolder(string root) => Root = root;

    /// <summary>Gets the absolute path a host would pass to UseSkills.</summary>
    public string Root { get; }

    /// <summary>Creates an empty folder.</summary>
    /// <returns>The folder.</returns>
    public static SkillFolder Create()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "agentcore-skills-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(root);
        return new SkillFolder(root);
    }

    /// <summary>Writes one skill directory.</summary>
    /// <param name="relativePath">Where to put it, relative to the root. May contain one '/'.</param>
    /// <param name="frontmatterName">The name to write in the frontmatter, or null to use the directory name.</param>
    /// <param name="description">The description the prompt advertises.</param>
    /// <returns>This folder, so a test chains its calls.</returns>
    public SkillFolder WithSkill(
        string relativePath,
        string? frontmatterName = null,
        string description = "A skill used by a test.")
    {
        var directory = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);

        var name = frontmatterName ?? Path.GetFileName(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\nDo the thing.\n");

        return this;
    }

    /// <summary>Adds a script to a skill written earlier, so a test can prove scripts stay hidden.</summary>
    /// <param name="relativePath">The skill's path, relative to the root.</param>
    /// <param name="fileName">The script file name.</param>
    /// <returns>This folder, so a test chains its calls.</returns>
    public SkillFolder WithScript(string relativePath, string fileName = "hello.py")
    {
        var scripts = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar), "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, fileName), "print('hi')\n");

        return this;
    }

    /// <summary>Adds a reference document to a skill written earlier.</summary>
    /// <param name="relativePath">The skill's path, relative to the root.</param>
    /// <param name="fileName">The file name.</param>
    /// <returns>This folder, so a test chains its calls.</returns>
    public SkillFolder WithReference(string relativePath, string fileName = "notes.md")
    {
        var references = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar), "references");
        Directory.CreateDirectory(references);
        File.WriteAllText(Path.Combine(references, fileName), "# notes\n");

        return this;
    }

    /// <summary>Reads exactly what load_skill would return for one skill.</summary>
    /// <param name="source">The source to read through.</param>
    /// <param name="name">The skill name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The body, including the resource and script manifests.</returns>
    public static async Task<string> LoadBodyAsync(
        AgentSkillsSource source,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        using ThrowingChatClient client = new();
        ChatClientAgent agent = new(client, new ChatClientAgentOptions { Name = "skill-probe" });

        var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, null), cancellationToken);
        var skill = skills.Single(candidate => string.Equals(candidate.Frontmatter.Name, name, StringComparison.Ordinal));

        return await skill.GetContentAsync(cancellationToken);
    }

    /// <summary>Deletes the folder.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // A test may have deleted it to exercise a missing folder.
        }
    }

    /// <summary>
    /// The framework's enumeration context refuses a null agent, and an agent needs a chat client.
    /// Nothing calls the model while skills are enumerated, so both response methods throw rather
    /// than answer.
    /// </summary>
    private sealed class ThrowingChatClient : IChatClient
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
