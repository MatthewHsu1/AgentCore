using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// The context providers behind one agent's harness switches (<c>todos:</c>, <c>mode:</c>,
/// <c>memory:</c>), added into the provider list <see cref="AgentContextProviderCompiler"/> builds.
/// </summary>
internal static class AgentHarnessProviders
{
    /// <summary>Adds this agent's harness providers, in declaration order: todos, mode, memory.</summary>
    /// <param name="providers">The provider list under construction.</param>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="item">The agent being compiled.</param>
    /// <param name="context">The compile-time seams, including the bound workspace root.</param>
    /// <param name="pointer">This agent's JSON pointer, for a <c>memory:</c> failure.</param>
    public static void Add(
        List<AIContextProvider> providers,
        AgentDefaults? defaults,
        AgentConfiguration item,
        AgentCompilationContext context,
        string pointer)
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
    }

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
            return new FileMemoryProvider(new FileSystemAgentFileStore(root), InitializeWorkingFolder);
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

    /// <summary>
    /// Builds the state a new <see cref="FileMemoryProvider"/> session starts with: its working
    /// folder set to the id of the call running the turn, so it reads and writes under
    /// <c>&lt;root&gt;/&lt;callId&gt;/</c> — the folder the host creates and deletes with the call.
    /// </summary>
#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.
    private static FileMemoryState InitializeWorkingFolder(AgentSession? session)
    {
        if (TurnAmbients.Current?.CallId is not { } callId)
        {
            throw new InvalidOperationException(
                "A memory: block's FileMemoryProvider needs the running call's id, and no turn is "
                + "open on this flow of execution. The provider's working folder is bound only while "
                + "a turn runs through a CallSession. Run the agent through a CallSession.");
        }

        return new FileMemoryState { WorkingFolder = callId };
    }
#pragma warning restore MAAI001
}
