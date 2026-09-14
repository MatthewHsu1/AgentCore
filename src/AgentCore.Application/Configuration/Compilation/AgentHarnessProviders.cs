using System.Text.RegularExpressions;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// The context providers behind one agent's harness switches (<c>todos:</c>, <c>mode:</c>,
/// <c>memory:</c>, <c>files:</c>, <c>shell:</c>, <c>background:</c>), added into the provider list
/// <see cref="AgentContextProviderCompiler"/> builds.
/// </summary>
internal static class AgentHarnessProviders
{
    /// <summary>
    /// MAF's default <see cref="FileMemoryProviderOptions.Instructions"/>, with its persistence claim
    /// replaced: the default tells the model these files "persist beyond the current conversation",
    /// which is false here — <c>FileMemoryProvider</c>'s working folder is the call's workspace,
    /// deleted at <c>EndCall</c>. Every other sentence, including how to use the tools, is unchanged.
    /// </summary>
    private const string MemoryInstructions =
        "## File Based Memory\n"
        + "You have access to a file-based memory system via the `file_memory_*` tools for storing and retrieving information across interactions.\n"
        + "These files act as your working memory for this call: they live in this call's workspace and are deleted when the call ends,\n"
        + "so anything you write now stays available for the rest of this call, but not after it ends.\n"
        + "Use these tools to store plans, memories, processing results, or downloaded data.\n\n"
        + "- Use descriptive file names (e.g., \"projectarchitecture.md\", \"userpreferences.md\").\n"
        + "- Include a description when writing a file to help with future discovery.\n"
        + "- Before starting new tasks, use file_memory_ls and file_memory_grep to check for relevant existing memories to avoid duplicate work.\n"
        + "- Keep memories up-to-date by overwriting files when information changes, or by using file_memory_replace and file_memory_replace_lines to make small edits.\n"
        + "- When you receive large amounts of data (e.g., downloaded web pages, API responses, research results),\n"
        + "  write them to files if they will be required later, so that they are not lost when older context is compacted or truncated.\n"
        + "  This ensures important data remains accessible across long-running sessions.";

    /// <summary>
    /// MAF's default <see cref="FileAccessProviderOptions.Instructions"/>, with its persistence claim
    /// replaced: the default tells the model these files "persist beyond the current session" and
    /// "may be shared across sessions or agents", which is false here — <c>CallFilesProvider</c>
    /// resolves to the call's workspace, deleted at <c>EndCall</c>. Every other sentence, including
    /// how to use the tools, is unchanged.
    /// </summary>
    private const string FilesInstructions =
        "## File Access\n"
        + "You have access to a shared file storage area via the `file_access_*` tools for reading, writing, and managing files.\n"
        + "These files live in this call's workspace: they exist only for the duration of this call and are deleted when the call ends.\n"
        + "Use these tools to read input data provided by the user, write output artifacts, and manage any files the user has asked you to work with.\n\n"
        + "- Never delete or overwrite existing files unless the user has explicitly asked you to do so.\n"
        + "- Files may be organized into subdirectories. Use `file_access_ls` to explore the tree level by level,\n"
        + "  or `file_access_grep` to search file contents recursively across the whole store.\n"
        + "- To make small edits to an existing file, prefer `file_access_replace` (substring replacement) or\n"
        + "  `file_access_replace_lines` (whole-line replacement) over rewriting the whole file.\n"
        + "- To change part of a file, find the line numbers with `file_access_grep`, read the range around them\n"
        + "  with `file_access_read_lines`, then edit with `file_access_replace_lines`. Reading the whole file\n"
        + "  first is rarely necessary.";

#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.

    /// <summary>Adds this agent's harness providers, in declaration order: todos, mode, memory, files, shell, background.</summary>
    /// <param name="providers">The provider list under construction.</param>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="item">The agent being compiled.</param>
    /// <param name="context">The compile-time seams, including the bound workspace root.</param>
    /// <param name="pointer">This agent's JSON pointer, for a <c>memory:</c>, <c>files:</c>, <c>shell:</c>, or <c>background:</c> failure.</param>
    /// <param name="resolve">Resolves an <c>agents.items</c> id to its compiled agent, or <see langword="null"/> when undeclared.</param>
    /// <param name="background">Collects the built background providers, so the call can release their sessions when it ends.</param>
    public static void Add(
        List<AIContextProvider> providers,
        AgentDefaults? defaults,
        AgentConfiguration item,
        AgentCompilationContext context,
        string pointer,
        Func<string, AIAgent?> resolve,
        ICollection<BackgroundAgentsProvider>? background = null)
    {
        var harness = AgentHarness.Compose(defaults, item);

        if (harness.Todos)
        {
            providers.Add(new TodoProvider());
        }

        if (harness.Mode)
        {
            providers.Add(new AgentModeProvider());
        }

        if (item.Memory is { Store: AgentFileStoreKind.Workspace })
        {
            providers.Add(BuildMemoryProvider(item, context, pointer));
        }

        if (item.Files is { Store: AgentFileStoreKind.Workspace } files)
        {
            providers.Add(BuildFilesProvider(item, files, context, pointer));
        }

        if (item.Shell is { } shell)
        {
            var options = BuildShellOptions(item, shell, context, pointer);
            providers.Add(new CallShellProvider(options));
            providers.Add(new CallShellEnvironmentProvider(options));
        }

        if (item.Background.Count > 0)
        {
            var provider = BuildBackgroundProvider(item, pointer, resolve);
            providers.Add(provider);
            background?.Add(provider);
        }
    }
#pragma warning restore MAAI001

#pragma warning disable MAAI001 // The loop family is evaluation-only in Microsoft.Agents.AI 1.21.0.

    /// <summary>
    /// Wraps one compiled agent in its <c>loop:</c> block: a <see cref="LoopAgent"/> over the
    /// <c>until:</c> evaluators in document order, capped at <c>maxRounds:</c>. No block, no wrap.
    /// </summary>
    /// <param name="agent">The compiled agent.</param>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="item">The agent being compiled.</param>
    /// <param name="pointer">This agent's JSON pointer, for a <c>loop:</c> failure.</param>
    /// <returns>The loop-wrapped agent, or <paramref name="agent"/> unchanged.</returns>
    public static AIAgent ApplyLoop(AIAgent agent, AgentDefaults? defaults, AgentConfiguration item, string pointer)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(item);

        if (item.Loop is not { } loop)
        {
            return agent;
        }

        if (loop.Until is not { Count: > 0 })
        {
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "loop"),
                $"the agent '{item.Id}' declares a loop: block with no until: entries, so the loop would "
                + "run exactly once and maxRounds: would never apply. Name what ends the loop, or remove "
                + "the loop: block.");
        }

        var harness = AgentHarness.Compose(defaults, item);
        List<LoopEvaluator> evaluators = new(loop.Until.Count);

        for (var index = 0; index < loop.Until.Count; index++)
        {
            var untilPointer = ConfigurationError.AppendPointer(
                ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(pointer, "loop"), "until"), index);

            var until = loop.Until[index];

            if (until.Todos is not null)
            {
                if (!harness.Todos)
                {
                    throw ConfigurationCompiler.Fail(
                        untilPointer,
                        $"the agent '{item.Id}' declares loop: until: todos: and no todos: switch, so the "
                        + "evaluator would fault every turn resolving its provider. Set todos: true, or "
                        + "remove the entry.");
                }

                evaluators.Add(new TodoCompletionLoopEvaluator(new TodoCompletionLoopEvaluatorOptions()));
            }

            if (until.Background is not null)
            {
                if (item.Background.Count == 0)
                {
                    throw ConfigurationCompiler.Fail(
                        untilPointer,
                        $"the agent '{item.Id}' declares loop: until: background: and no background: children, "
                        + "so the evaluator would fault every turn resolving its provider. Name the children, "
                        + "or remove the entry.");
                }

                evaluators.Add(new BackgroundTaskCompletionLoopEvaluator(new BackgroundTaskCompletionLoopEvaluatorOptions()));
            }
        }

        // Every iteration's text reaches the stream; there are no per-iteration entries (decision 14).
        return new LoopAgent(agent, evaluators, new LoopAgentOptions { MaxIterations = loop.MaxRounds });
    }

#pragma warning restore MAAI001

#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.

    /// <summary>
    /// The state keys of the harness providers among <paramref name="providers"/> — never the
    /// history provider's key, which is the transcript and belongs to store 1.
    /// </summary>
    internal static IReadOnlySet<string> StateKeysOf(IEnumerable<AIContextProvider> providers)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            if (provider is not (TodoProvider or AgentModeProvider or FileMemoryProvider or FileAccessProvider or BackgroundAgentsProvider))
            {
                continue;
            }

            foreach (var key in provider.StateKeys)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

#pragma warning restore MAAI001

#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.

    private static BackgroundAgentsProvider BuildBackgroundProvider(
        AgentConfiguration item,
        string pointer,
        Func<string, AIAgent?> resolve)
    {
        if (item.Background.Contains(item.Id, StringComparer.Ordinal))
        {
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "background"),
                $"the agent '{item.Id}' names itself in background:, so it would start itself as its own child. "
                + "Remove it from the list, or point at another agent.");
        }

        List<AIAgent> children = new(item.Background.Count);
        for (var index = 0; index < item.Background.Count; index++)
        {
            var childPointer = ConfigurationError.AppendPointer(
                ConfigurationError.AppendPointer(pointer, "background"), index);

            var childId = item.Background[index];

            children.Add(resolve(childId)
                ?? throw ConfigurationCompiler.Fail(
                    childPointer,
                    $"the agent '{childId}' is not declared in agents.items"));
        }

        try
        {
            // Children run with their own compiled tools, which carry no approval-required tool
            // today — every harness tool runs with approval off — so a child never stalls waiting
            // for an approval the parent would only see as a completed task with empty output.
            return new BackgroundAgentsProvider(children);
        }
        catch (ArgumentException exception)
        {
            // The provider keys children by name case-insensitively, so two ids that differ only
            // by case collide here rather than at either declaration.
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "background"),
                $"the agent '{item.Id}' declares background: children whose names collide: {exception.Message}");
        }
    }

#pragma warning restore MAAI001

    private static FileMemoryProvider BuildMemoryProvider(
        AgentConfiguration item, AgentCompilationContext context, string pointer)
    {
        if (context.WorkspaceRoot is not { } root)
        {
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "memory"),
                $"the agent '{item.Id}' declares a memory: block and this host bound no workspace "
                + "root, so there is nowhere to keep the files. Call options.UseWorkspace(...) with "
                + "the folder, or remove the memory: block.");
        }

        try
        {

#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.
            return new FileMemoryProvider(
                new FileSystemAgentFileStore(root),
                InitializeWorkingFolder,
                new FileMemoryProviderOptions { Instructions = MemoryInstructions });
#pragma warning restore MAAI001

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // FileSystemAgentFileStore's constructor creates the root eagerly, and this compiles
            // before CallSessionStartup's own guarded create — so a bad root fails here first, and
            // must fail with the same named cause an operator gets from that later guard.
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "memory"),
                $"the agent '{item.Id}' declares a memory: block and the workspace root '{root}' "
                + $"could not be created: {exception.Message} Check the path passed to "
                + "options.UseWorkspace(...), and that the process has permission to create it.");
        }
    }

#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.

    private static CallFilesProvider BuildFilesProvider(
        AgentConfiguration item,
        AgentFilesConfiguration files,
        AgentCompilationContext context,
        string pointer)
    {
        if (context.WorkspaceRoot is null)
        {
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "files"),
                $"the agent '{item.Id}' declares a files: block and this host bound no workspace "
                + "root, so there is nowhere to keep the files. Call options.UseWorkspace(...) with "
                + "the folder, or remove the files: block.");
        }

        return new CallFilesProvider(
            context.WorkspaceRoot,
            new FileAccessProviderOptions
            {
                DisableWriteTools = !files.Write,
                DisableReadOnlyToolApproval = true,
                DisableWriteToolApproval = true,
                Instructions = FilesInstructions,
            });
    }

#pragma warning restore MAAI001

    private static CallShellOptions BuildShellOptions(
        AgentConfiguration item,
        ShellConfiguration shell,
        AgentCompilationContext context, string pointer)
    {
        if (context.WorkspaceRoot is null)
        {
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "shell"),
                $"the agent '{item.Id}' declares a shell: block and this host bound no workspace "
                + "root, so the shell has no working directory. Call options.UseWorkspace(...) with "
                + "the folder, or remove the shell: block.");
        }

        var policy = shell.Policy is { } declared ? BuildPolicy(item, declared, pointer) : null;

        var timeout = shell.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;

        return new CallShellOptions(shell.Kind, policy, timeout);
    }

    private static ShellPolicy BuildPolicy(
        AgentConfiguration item,
        ShellPolicyConfiguration policy,
        string pointer)
    {
        // ShellPolicy treats a supplied-but-empty allow list as deny-all (it denies any command that
        // matches none of the allow patterns, and an empty list matches nothing), so an agent that
        // declares no allow: must reach the executor with allowList: null, not an empty collection.
        var allow = policy.Allow.Count == 0 ? null : policy.Allow;

        try
        {
            return new ShellPolicy(denyList: policy.Deny, allowList: allow);
        }
        catch (ArgumentException exception)
        {
            // ShellPolicy compiles every pattern into a Regex eagerly in its own constructor, so a
            // bad pattern in either list throws here, at compile time, rather than at the model's
            // first command.
            var bad = policy.Deny.Concat(allow ?? [])
                .FirstOrDefault(pattern => !IsValidRegex(pattern));

            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(pointer, "shell"), "policy"),
                $"the agent '{item.Id}' declares a shell: policy whose pattern '{bad}' is not a "
                + $"valid regex: {exception.Message}");
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.

    /// <summary>
    /// Builds the state a new <see cref="FileMemoryProvider"/> session starts with: its working
    /// folder set to the id of the call running the turn, so it reads and writes under
    /// <c>&lt;root&gt;/&lt;callId&gt;/</c> — the folder the host creates and deletes with the call.
    /// </summary>
    private static FileMemoryState InitializeWorkingFolder(AgentSession? session)
    {
        if (TurnRegistry.For(session)?.CallId is not { } callId)
        {
            throw new InvalidOperationException(
                "A memory: block's FileMemoryProvider needs the running call's id, and no turn is "
                + "filed for this session. The provider's working folder is bound only while "
                + "a turn runs through a CallSession. Run the agent through a CallSession.");
        }

        return new FileMemoryState { WorkingFolder = callId };
    }

#pragma warning restore MAAI001
}
