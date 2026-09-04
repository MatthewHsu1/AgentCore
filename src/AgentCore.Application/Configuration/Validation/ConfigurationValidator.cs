using System.Globalization;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// Checks 2 to 8 of section 8.5, over one bound document.
/// </summary>
/// <remarks>
/// <para>
/// Check 1 runs in <see cref="ConfigurationSchemaValidator"/>, before the document binds. This type
/// runs every later check and reports all of them at once, so one load names every defect.
/// </para>
/// <para>
/// MAF performs none of these checks. This validator is what makes <c>graph:</c> as safe as
/// <c>policy:</c>, and it is therefore what lets AgentCore expose both.
/// </para>
/// <para>
/// Decision 15 splits check 2 in two. <see cref="EvaluateStructure"/> runs every check that does not
/// depend on which tools MCP discovery ends up serving, so a YAML typo never costs a round trip to
/// every MCP server. <see cref="ValidateToolReferences"/> resolves tool ids afterwards, against
/// whatever the tool registry actually serves. <see cref="Evaluate"/> and <see cref="Validate"/> still
/// run both passes together, against the ids <c>tools:</c> declares, so every caller that predates MCP
/// keeps its current meaning.
/// </para>
/// </remarks>
public static class ConfigurationValidator
{
    /// <summary>
    /// The longest interval, in seconds, that every timer these values reach will accept.
    /// <c>CancellationTokenSource.CancelAfter</c>, <c>Task.WaitAsync</c> and <c>PeriodicTimer</c> each
    /// throw above <see cref="int.MaxValue"/> milliseconds, from a call site that carries no pointer
    /// into the document.
    /// </summary>
    private const int MaxIntervalSeconds = int.MaxValue / 1000;

    /// <summary>Runs checks 2 to 8 and returns everything they find.</summary>
    /// <param name="configuration">The bound document.</param>
    /// <returns>Every error and every partial-coverage warning.</returns>
    public static ConfigurationValidationResult Evaluate(AgentCoreConfiguration configuration)
    {
        var structural = EvaluateStructure(configuration);

        var declaredToolIds = configuration.Tools.Select(static tool => tool.Id).ToHashSet(StringComparer.Ordinal);
        var toolErrors = new List<ConfigurationError>();
        CheckToolReferences(configuration, declaredToolIds, toolErrors);

        if (toolErrors.Count == 0)
        {
            return structural;
        }

        return new ConfigurationValidationResult
        {
            Errors = [.. structural.Errors, .. toolErrors],
            Warnings = structural.Warnings,
        };
    }

    /// <summary>Runs checks 2 to 8 and throws when any of them fails.</summary>
    /// <param name="configuration">The bound document.</param>
    /// <returns>The result, so a caller can read the partial-coverage warnings of check 5.</returns>
    /// <exception cref="ConfigurationLoadException">The document fails one or more checks.</exception>
    public static ConfigurationValidationResult Validate(AgentCoreConfiguration configuration)
    {
        var result = Evaluate(configuration);
        if (result.Errors.Count > 0)
        {
            throw new ConfigurationLoadException(result.Errors);
        }

        return result;
    }

    /// <summary>
    /// Runs every check of section 8.5 except tool-reference resolution, and returns everything they
    /// find.
    /// </summary>
    /// <param name="configuration">The bound document.</param>
    /// <returns>Every error and every partial-coverage warning.</returns>
    public static ConfigurationValidationResult EvaluateStructure(AgentCoreConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<ConfigurationError>();
        var warnings = new List<ConfigurationError>();
        var names = DeclaredNames.From(configuration);

        CheckReferences(configuration, names, errors);
        CheckSlotWriters(configuration, errors);
        CheckKnowledgeScopeSlots(configuration, errors);
        CheckVocabularyAndAmbiguity(configuration, errors, warnings);
        CheckGuardRules(configuration, errors);
        CheckExclusivity(configuration, errors, warnings);
        CheckReachability(configuration, errors);
        CheckGraphWellFormedness(configuration, errors);
        CheckDelegationCycles(configuration, errors);
        CheckMcpServerIds(configuration, errors);
        CheckMcpSecretPlacement(configuration, errors);

        if (errors.Count == 0 && warnings.Count == 0)
        {
            return ConfigurationValidationResult.Clean;
        }

        return new ConfigurationValidationResult
        {
            Errors = errors,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Runs every check of section 8.5 except tool-reference resolution, and throws when any of them
    /// fails.
    /// </summary>
    /// <param name="configuration">The bound document.</param>
    /// <returns>The result, so a caller can read the partial-coverage warnings of check 5.</returns>
    /// <exception cref="ConfigurationLoadException">The document fails one or more checks.</exception>
    public static ConfigurationValidationResult ValidateStructure(AgentCoreConfiguration configuration)
    {
        var result = EvaluateStructure(configuration);
        if (result.Errors.Count > 0)
        {
            throw new ConfigurationLoadException(result.Errors);
        }

        return result;
    }

    /// <summary>
    /// Resolves every tool reference in the document against the ids the tool registry actually
    /// serves, and throws when one names an id nothing serves.
    /// </summary>
    /// <param name="configuration">The bound document.</param>
    /// <param name="servedToolIds">Every tool id the registry serves, declared and MCP-discovered alike.</param>
    /// <exception cref="ConfigurationLoadException">A reference names a tool id nothing serves.</exception>
    public static void ValidateToolReferences(AgentCoreConfiguration configuration, IReadOnlySet<string> servedToolIds)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(servedToolIds);

        var errors = new List<ConfigurationError>();
        CheckToolReferences(configuration, servedToolIds, errors);

        if (errors.Count > 0)
        {
            throw new ConfigurationLoadException(errors);
        }
    }

    /// <summary>
    /// Resolves every slot's <c>vocabulary.linker</c> against the names the linker registry actually
    /// serves, and throws when one names a linker nothing registered.
    /// </summary>
    /// <remarks>
    /// K12: a two-argument validator, run after the linker registry is built — the registry itself
    /// lives in Application and is not reachable from here. A caller runs this after building it,
    /// the same way <see cref="ValidateToolReferences"/> runs after MCP discovery.
    /// </remarks>
    /// <param name="configuration">The bound document.</param>
    /// <param name="registered">Every linker name the registry serves. Always includes <c>exact</c>.</param>
    /// <exception cref="ConfigurationLoadException">A slot names a linker nothing registered.</exception>
    public static void ValidateLinkerNames(AgentCoreConfiguration configuration, IReadOnlySet<string> registered)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registered);

        var errors = new List<ConfigurationError>();
        CheckLinkerReferences(configuration, registered, errors);

        if (errors.Count > 0)
        {
            throw new ConfigurationLoadException(errors);
        }
    }

    /// <summary>
    /// Resolves every <c>skills:</c> entry against the names the bound skills folder serves, and
    /// throws when one names a skill nothing serves.
    /// </summary>
    /// <param name="configuration">The bound document.</param>
    /// <param name="servedSkillNames">Every skill name the bound folder serves.</param>
    /// <exception cref="ConfigurationLoadException">A reference names a skill nothing serves.</exception>
    public static void ValidateSkillReferences(AgentCoreConfiguration configuration, IReadOnlySet<string> servedSkillNames)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(servedSkillNames);

        var errors = new List<ConfigurationError>();
        CheckSkillReferences(configuration, servedSkillNames, errors);

        if (errors.Count > 0)
        {
            throw new ConfigurationLoadException(errors);
        }
    }

    /// <summary>
    /// Refuses a declared tool id that the skills provider also registers. The provider's tools are
    /// added per agent and never pass through the tool registry, so a collision is invisible until
    /// the model receives two tools of one name.
    /// </summary>
    /// <param name="configuration">The bound document.</param>
    /// <exception cref="ConfigurationLoadException">A tool id collides with a skills tool name.</exception>
    public static void ValidateSkillToolNames(AgentCoreConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<ConfigurationError>();
        CheckSkillToolNames(configuration, errors);

        if (errors.Count > 0)
        {
            throw new ConfigurationLoadException(errors);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2: reference resolution.
    // ---------------------------------------------------------------------------------------------
    private static void CheckReferences(AgentCoreConfiguration configuration, DeclaredNames names, List<ConfigurationError> errors)
    {
        // A kind: agent tool names the agent it runs, and that name resolves whether or not any agent
        // lists the tool. The compiler catches only the listed case, and only after the load passes.
        for (var index = 0; index < configuration.Tools.Count; index++)
        {
            var tool = configuration.Tools[index];
            if (tool.Kind == ToolKind.Agent && tool.Agent is { } target && !names.Agents.Contains(target))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(Pointer.Tool(index), "agent"),
                    $"the agent '{target}' is not declared in agents.items"));
            }
        }

        if (configuration.Extractor is { } extractor)
        {
            AddUnknownModel(extractor.Model, "/extractor/model/ref", names, errors);
        }

        if (configuration.Evaluation is { } evaluation)
        {
            AddUnknownModel(evaluation.Judge, "/evaluation/judge/ref", names, errors);
        }

        if (configuration.Titler is { } titler)
        {
            AddUnknownModel(titler.Model, "/titler/model/ref", names, errors);
        }

        var items = configuration.Agents?.Items ?? [];
        if (configuration.Agents?.Defaults is { } defaults)
        {
            AddUnknownModel(defaults.Model, "/agents/defaults/model/ref", names, errors);
        }

        for (var index = 0; index < items.Count; index++)
        {
            var agent = items[index];
            AddUnknownModel(
                agent.Model,
                ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(Pointer.Agent(index), "model"), "ref"),
                names,
                errors);
        }

        if (configuration.Policy is { } policy)
        {
            if (!names.Stages.Contains(policy.Initial))
            {
                errors.Add(Reference("/policy/initial", $"the stage '{policy.Initial}' is not declared in policy.stages"));
            }

            for (var index = 0; index < policy.Stages.Count; index++)
            {
                var stage = policy.Stages[index];
                if (stage.Agent is { } agentId && !names.Agents.Contains(agentId))
                {
                    errors.Add(Reference(
                        ConfigurationError.AppendPointer(Pointer.Stage(index), "agent"),
                        $"the agent '{agentId}' is not declared in agents.items"));
                }

                for (var exit = 0; exit < stage.To.Count; exit++)
                {
                    var transition = stage.To[exit];
                    var pointer = Pointer.Transition(index, exit);
                    if (!names.Stages.Contains(transition.Stage))
                    {
                        errors.Add(Reference(
                            ConfigurationError.AppendPointer(pointer, "stage"),
                            $"the stage '{transition.Stage}' is not declared in policy.stages"));
                    }

                    AddUnknownGuard(transition.When, ConfigurationError.AppendPointer(pointer, "when"), names, errors);
                }
            }
        }

        if (configuration.Graph is not { } graph)
        {
            return;
        }

        for (var index = 0; index < graph.Agents.Count; index++)
        {
            if (!names.Agents.Contains(graph.Agents[index]))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer("/graph/agents", index),
                    $"the agent '{graph.Agents[index]}' is not declared in agents.items"));
            }
        }

        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            if (graph.Nodes[index].Agent is { } agentId && !names.Agents.Contains(agentId))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(Pointer.Node(index), "agent"),
                    $"the agent '{agentId}' is not declared in agents.items"));
            }
        }

        for (var index = 0; index < graph.Edges.Count; index++)
        {
            var edge = graph.Edges[index];
            var pointer = Pointer.Edge(index);
            if (!names.Nodes.Contains(edge.From))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "from"),
                    $"the node '{edge.From}' is not declared in graph.nodes"));
            }

            if (!names.Nodes.Contains(edge.To))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "to"),
                    $"the node '{edge.To}' is not declared in graph.nodes"));
            }

            AddUnknownGuard(edge.When, ConfigurationError.AppendPointer(pointer, "when"), names, errors);
        }
    }

    private static void AddUnknownGuard(GuardReference? guard, string pointer, DeclaredNames names, List<ConfigurationError> errors)
    {
        if (guard?.Name is { } name && !names.Guards.Contains(name))
        {
            errors.Add(Reference(pointer, $"the guard '{name}' is not declared in guards:"));
        }
    }

    /// <summary>Resolves one model reference against the <c>as:</c> names of <c>providers.llm</c>.</summary>
    /// <remarks>
    /// An absent <c>providers:</c> section, or an absent <c>providers.llm</c>, declares no model
    /// name, so every reference into it is unknown. That is how check 2 already reads an absent
    /// <c>tools:</c> and an absent <c>agents:</c>, and a model reference is the sixth reference kind.
    /// A document that names no model declares nothing to resolve and stays clean.
    /// </remarks>
    private static void AddUnknownModel(ModelReference? model, string pointer, DeclaredNames names, List<ConfigurationError> errors)
    {
        if (model is { } reference && !names.Models.Contains(reference.Ref))
        {
            errors.Add(Reference(pointer, $"the model '{reference.Ref}' is not declared in providers.llm"));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2, tool ids: the other half of reference resolution.
    // ---------------------------------------------------------------------------------------------
    /// <summary>
    /// Resolves the two reference kinds that name a tool id: a <c>from:</c> state slot and an agent's
    /// <c>tools:</c> entry. Decision 15 keeps this separate from <see cref="CheckReferences"/> because
    /// these are the only reference kinds an MCP server's discovery can satisfy, so this is the only
    /// half of check 2 that has to wait for it.
    /// </summary>
    private static void CheckToolReferences(AgentCoreConfiguration configuration, IReadOnlySet<string> servedToolIds, List<ConfigurationError> errors)
    {
        foreach (var slot in configuration.State)
        {
            if (slot.Value.From is { } from && !servedToolIds.Contains(from.ToolId))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(Pointer.State(slot.Key), "from"),
                    $"nothing serves the tool '{from.ToolId}'. Declare it in tools:, or check that an mcp: server offers it."));
            }
        }

        var items = configuration.Agents?.Items ?? [];
        for (var index = 0; index < items.Count; index++)
        {
            var agent = items[index];
            for (var slot = 0; slot < agent.Tools.Count; slot++)
            {
                if (!servedToolIds.Contains(agent.Tools[slot]))
                {
                    errors.Add(Reference(
                        ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(Pointer.Agent(index), "tools"), slot),
                        $"nothing serves the tool '{agent.Tools[slot]}'. Declare it in tools:, or check that an mcp: server offers it."));
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2, linker names: K12's two-argument validator, run after the linker registry is built.
    // ---------------------------------------------------------------------------------------------
    /// <summary>Resolves every declared <c>vocabulary.linker</c> against what the registry serves.</summary>
    /// <param name="configuration">The bound document.</param>
    /// <param name="registered">Every linker name the registry serves.</param>
    /// <param name="errors">The list every failure is added to.</param>
    private static void CheckLinkerReferences(
        AgentCoreConfiguration configuration, IReadOnlySet<string> registered, List<ConfigurationError> errors)
    {
        foreach (var slot in configuration.State)
        {
            if (slot.Value.Vocabulary is not { } vocabulary || registered.Contains(vocabulary.Linker))
            {
                continue;
            }

            errors.Add(Reference(
                ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(Pointer.State(slot.Key), "vocabulary"), "linker"),
                $"the slot '{slot.Key}' declares vocabulary.linker: '{vocabulary.Linker}', which nothing "
                + "registered. Register it with UseStateValueLinkers, or use 'exact'."));
        }
    }

    /// <summary>Resolves every agent's <c>skills:</c> entry against what the bound folder serves.</summary>
    /// <param name="configuration">The bound document.</param>
    /// <param name="servedSkillNames">Every skill name the bound folder serves.</param>
    /// <param name="errors">The list every failure is added to.</param>
    private static void CheckSkillReferences(
        AgentCoreConfiguration configuration,
        IReadOnlySet<string> servedSkillNames,
        List<ConfigurationError> errors)
    {
        var served = string.Join(", ", servedSkillNames.Order(StringComparer.Ordinal));
        var items = configuration.Agents?.Items ?? [];

        for (var index = 0; index < items.Count; index++)
        {
            var agent = items[index];
            for (var slot = 0; slot < agent.Skills.Count; slot++)
            {
                if (servedSkillNames.Contains(agent.Skills[slot]))
                {
                    continue;
                }

                errors.Add(Reference(
                    ConfigurationError.AppendPointer(
                        ConfigurationError.AppendPointer(Pointer.Agent(index), "skills"), slot),
                    $"the skill '{agent.Skills[slot]}' is not in the bound skills folder. "
                    + $"The folder serves: {served}."));
            }
        }
    }

    /// <summary>Refuses a declared tool id that the skills provider registers under the same name.</summary>
    /// <param name="configuration">The bound document.</param>
    /// <param name="errors">The list every failure is added to.</param>
    private static void CheckSkillToolNames(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        var items = configuration.Agents?.Items ?? [];
        if (!items.Any(agent => agent.Skills.Count > 0))
        {
            return;
        }

        string[] reserved =
        [
            AgentSkillsProvider.LoadSkillToolName,
            AgentSkillsProvider.ReadSkillResourceToolName,
            AgentSkillsProvider.RunSkillScriptToolName,
        ];

        var tools = configuration.Tools;
        for (var index = 0; index < tools.Count; index++)
        {
            if (!reserved.Contains(tools[index].Id, StringComparer.Ordinal))
            {
                continue;
            }

            errors.Add(Reference(
                ConfigurationError.AppendPointer(Pointer.Tool(index), "id"),
                $"the tool id '{tools[index].Id}' is reserved while any agent declares a skills: "
                + "list, because the skills provider registers a tool of that name. Rename the tool."));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2, mcp: ids: a duplicate connects twice and its collision surfaces only at boot, naming
    // the served tool id rather than the mcp: entry that caused it. This runs before any connection
    // opens, so it costs nothing to check here.
    // ---------------------------------------------------------------------------------------------
    private static void CheckMcpServerIds(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < configuration.Mcp.Count; index++)
        {
            var server = configuration.Mcp[index];
            if (!seen.Add(server.Id))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(Pointer.Mcp(index), "id"),
                    $"two mcp: entries declare the id '{server.Id}'. An id names one server, so rename "
                    + "one of them."));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // mcp: secret placement. command: becomes the child's argv, which every user on the box can read
    // out of ps, and url: is logged by proxies and reverse proxies along the way. Neither is a
    // SecretTemplate, so a reference written there would be passed through as its literal characters
    // and would leak while not even working. The schema cannot express "this string may not hold that
    // substring", so it is checked here.
    // ---------------------------------------------------------------------------------------------
    private static void CheckMcpSecretPlacement(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        for (var index = 0; index < configuration.Mcp.Count; index++)
        {
            var server = configuration.Mcp[index];

            for (var word = 0; word < server.Command.Count; word++)
            {
                if (!server.Command[word].Contains(SecretReference.Prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                errors.Add(Reference(
                    ConfigurationError.AppendPointer(
                        ConfigurationError.AppendPointer(Pointer.Mcp(index), "command"), word),
                    $"the mcp: server '{server.Id}' writes a ${{secret:...}} reference in command:. A "
                    + "command becomes the child process's argv, which every user on this machine can "
                    + "read out of ps, and nothing resolves a reference there — it would be passed "
                    + "through as its own characters. Put the credential in env: instead."));
            }

            if (server.Url is { } url && url.Contains(SecretReference.Prefix, StringComparison.Ordinal))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(Pointer.Mcp(index), "url"),
                    $"the mcp: server '{server.Id}' writes a ${{secret:...}} reference in url:. A URL is "
                    + "logged by every proxy it passes, and nothing resolves a reference there — it "
                    + "would be passed through as its own characters. Put the credential in headers: "
                    + "instead."));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2, knowledge scope: a state-built scope must not produce a filter nobody meant.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Refuses a scope built from state that could produce a filter nobody meant.</summary>
    /// <remarks>
    /// These are document checks, not store checks: <c>KnowledgeStartup</c> skips the store factory
    /// for a host-supplied port, but the session composes the scope either way.
    /// </remarks>
    private static void CheckKnowledgeScopeSlots(
        AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        if (configuration.Providers?.Knowledge?.Scope is not { } scope)
        {
            return;
        }

        if (scope.Wildcard is { } wildcard)
        {
            if (string.IsNullOrWhiteSpace(wildcard.Value))
            {
                errors.Add(Reference(
                    Pointer.WildcardValue,
                    "the wildcard value is blank, so it names no payload value a card could carry."));
            }

            if (wildcard.Facets.Count == 0)
            {
                errors.Add(Reference(
                    Pointer.WildcardFacets,
                    "the wildcard names no facets, so it widens nothing. Name the reach facets, and "
                    + "never an isolation facet such as a customer id."));
            }
        }

        // A wildcard without fromState is a supported shape: the deployment resolves its own facets
        // and opens them as the host ambient, which the store still widens. See
        // StateKnowledgeScope.Compose and KnowledgeStartup's K19 branch. Only the reverse is refused.
        if (scope.FromState.Count == 0)
        {
            return;
        }

        if (scope.Wildcard is not { } widened)
        {
            errors.Add(Reference(
                Pointer.Wildcard,
                "fromState is set and no wildcard is. An unknown slot would then leave its facet out "
                + "of the scope, putting no condition on it, and every value of that facet would be "
                + "in reach. Declare the wildcard, or drop fromState."));
        }
        else
        {
            foreach (var facet in widened.Facets)
            {
                if (!scope.FromState.Contains(facet, StringComparer.Ordinal))
                {
                    errors.Add(Reference(
                        Pointer.WildcardFacets,
                        $"wildcard.facets names '{facet}' and fromState does not. The scope filter only "
                        + $"ever puts a condition on a fromState facet, so there is no condition on "
                        + $"'{facet}' for the wildcard to widen."));
                }
            }

            foreach (var name in scope.FromState)
            {
                if (!widened.Facets.Contains(name, StringComparer.Ordinal))
                {
                    errors.Add(Reference(
                        Pointer.WildcardFacets,
                        $"fromState names '{name}' and wildcard.facets does not, so an unfilled '{name}' "
                        + "would be searched for the literal wildcard rather than widened by it."));
                }
            }
        }

        if (configuration.Extractor is null)
        {
            errors.Add(Reference(
                "/extractor",
                "fromState names extractor slots and this document declares no extractor, so no slot "
                + "is ever filled and every call searches only what the wildcard admits."));
        }

        foreach (var name in scope.FromState)
        {
            if (!configuration.State.TryGetValue(name, out var slot))
            {
                errors.Add(Reference(
                    Pointer.FromState,
                    $"fromState names the slot '{name}', which this document does not declare."));
                continue;
            }

            var pointer = Pointer.State(name);

            if (slot.Default is not null)
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "default"),
                    $"the facet slot '{name}' declares a default. An unfilled slot reads as its "
                    + "default, so every call before the caller says otherwise would be scoped to a "
                    + "guess, with no error."));
            }

            if (slot.Type != StateSlotType.String)
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "type"),
                    $"the facet slot '{name}' is not type string. A facet holds one string value."));
            }

            if (slot.Writer != StateWriter.Extractor)
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "writer"),
                    $"the facet slot '{name}' is not written by the extractor. A const slot is filled "
                    + "before turn 1 and would scope every call to it; a tool slot could change the "
                    + "scope mid-call."));
            }

            if (slot.EnumValues is not { Count: > 0 } && slot.Vocabulary is null)
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "enum"),
                    $"the facet slot '{name}' declares neither enum nor vocabulary. Nothing would then "
                    + "stop a value the corpus has never been tagged with, which the wildcard turns "
                    + "into an answer from the wrong bucket rather than an empty result."));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2, vocabulary and ambiguity: section 10 of the ambiguity-and-vocabulary design.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Refuses a <c>vocabulary:</c> or <c>ambiguity:</c> block that could not do what it declares.</summary>
    /// <remarks>
    /// Unlike <see cref="CheckKnowledgeScopeSlots"/>, the slot-level rules here do not depend on the
    /// slot being named in <c>scope.fromState</c>: a slot may declare <c>vocabulary:</c> without ever
    /// being read by the scope, and that mismatch is itself one of the refusals below.
    /// </remarks>
    private static void CheckVocabularyAndAmbiguity(
        AgentCoreConfiguration configuration, List<ConfigurationError> errors, List<ConfigurationError> warnings)
    {
        var knowledge = configuration.Providers?.Knowledge;
        var fromState = knowledge?.Scope.FromState ?? [];
        var anyVocabulary = false;

        foreach (var entry in configuration.State)
        {
            if (entry.Value.Vocabulary is not { } vocabulary)
            {
                continue;
            }

            anyVocabulary = true;
            var pointer = Pointer.State(entry.Key);
            var vocabularyPointer = ConfigurationError.AppendPointer(pointer, "vocabulary");

            if (entry.Value.EnumValues is { Count: > 0 })
            {
                errors.Add(Reference(
                    vocabularyPointer,
                    $"the slot '{entry.Key}' declares both enum and vocabulary. enum is a fixed, "
                    + "hand-written list; vocabulary reads the domain from a provider at boot. A slot "
                    + "cannot have both."));
            }

            if (entry.Value.Value is not null)
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(pointer, "value"),
                    $"the slot '{entry.Key}' declares both value and vocabulary. value is writer: "
                    + "const's fixed value; vocabulary reads a domain the extractor fills at runtime. "
                    + "A slot cannot have both."));
            }

            if (!fromState.Contains(entry.Key, StringComparer.Ordinal))
            {
                errors.Add(Reference(
                    ConfigurationError.AppendPointer(vocabularyPointer, "from"),
                    $"the slot '{entry.Key}' declares vocabulary.from: knowledge, and "
                    + "providers.knowledge.scope.fromState does not name it. The gate and the linker "
                    + "would then hold a domain no turn's scope ever narrows by."));
            }

            CheckRange(
                vocabulary.MaxValues,
                2,
                int.MaxValue,
                ConfigurationError.AppendPointer(vocabularyPointer, "maxValues"),
                $"vocabulary.maxValues on the slot '{entry.Key}'",
                "A read of fewer than two values could never be told apart from a truncated one.",
                errors);

            CheckRange(
                vocabulary.RefreshSeconds,
                0,
                MaxIntervalSeconds,
                ConfigurationError.AppendPointer(vocabularyPointer, "refreshSeconds"),
                $"vocabulary.refreshSeconds on the slot '{entry.Key}'",
                "0 means boot only. A negative interval matches AgentCoreBoot's own "
                + "{ RefreshSeconds: > 0 } guard on nothing, so the slot would silently read once at "
                + "boot and never refresh again, and one above the range throws out of the "
                + "PeriodicTimer VocabularyRefreshService builds from it.",
                errors);
        }

        if (anyVocabulary && knowledge?.Ambiguity is null)
        {
            errors.Add(Reference(
                Pointer.Ambiguity,
                "a slot declares vocabulary, and providers.knowledge.ambiguity is absent. vocabulary "
                + "installs the linker, which can return Ambiguous — an outcome a plain enum gate never "
                + "produces — and without ambiguity there is no channel to tell anyone."));
        }

        if (knowledge?.Ambiguity is not { } ambiguity)
        {
            return;
        }

        if (knowledge.Mapper is not null)
        {
            errors.Add(Reference(
                Pointer.Mapper,
                "providers.knowledge.ambiguity is declared, and providers.knowledge.mapper names a "
                + "custom mapper. The probe reads each card's facet values out of Extras, which only "
                + "the built-in field mapper fills, so every probe would find no values and the "
                + "channel would stay silent."));
        }

        if (knowledge.Scope.Wildcard is null)
        {
            errors.Add(Reference(
                Pointer.Ambiguity,
                "providers.knowledge.ambiguity is declared and providers.knowledge.scope.wildcard is "
                + "absent. The probe drops a facet the wildcard filled, so with no wildcard it has "
                + "nothing to drop and the channel can never fire."));
        }

        CheckRange(
            ambiguity.MaxCandidates,
            2,
            int.MaxValue,
            ConfigurationError.AppendPointer(Pointer.Ambiguity, "maxCandidates"),
            "ambiguity.maxCandidates",
            "Below 2, the ask could never name a spread of candidates.",
            errors);

        CheckRange(
            ambiguity.MaxAsks,
            0,
            int.MaxValue,
            ConfigurationError.AppendPointer(Pointer.Ambiguity, "maxAsks"),
            "ambiguity.maxAsks",
            "0 is legal and means gate only; a negative count is not.",
            errors);

        CheckRange(
            ambiguity.ProbeDeadlineSeconds,
            1,
            MaxIntervalSeconds,
            ConfigurationError.AppendPointer(Pointer.Ambiguity, "probeDeadlineSeconds"),
            "ambiguity.probeDeadlineSeconds",
            "A budget below one second leaves the probe unable to complete even the fastest real "
            + "search, and one above the range throws out of the CancelAfter that arms it.",
            errors);

        CheckRange(
            ambiguity.ProbeWaitMarginSeconds,
            1,
            MaxIntervalSeconds,
            ConfigurationError.AppendPointer(Pointer.Ambiguity, "probeWaitMarginSeconds"),
            "ambiguity.probeWaitMarginSeconds",
            "A margin of 0 reinstates the race it exists to close: the loser's wait would end as the "
            + "winner's own search does, leaving a margin equal to the arrival spread. Above the "
            + "range, the deadline this is added to no longer fits the wait it arms.",
            errors);

        // A probe drops one of the scope's own facets, so it needs a second one left to search by.
        // Zero is as unreachable as one, and reaches this line whenever the host supplies every facet.
        if (fromState.Count <= 1)
        {
            warnings.Add(Reference(
                Pointer.Ambiguity,
                "providers.knowledge.ambiguity is declared and scope.fromState names at most one "
                + "facet. That deployment has no droppable facet other than its only one, so the probe "
                + "is unreachable unless the host sets one too."));
        }

        if (configuration.Graph is not null)
        {
            warnings.Add(Reference(
                Pointer.Ambiguity,
                "providers.knowledge.ambiguity is declared on a graph: document. The clarification's "
                + "turn-context guard only passes on a session whose row carries history, which a "
                + "graph run does not, so channel 1 is silent here."));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 3: one writer for each slot.
    // ---------------------------------------------------------------------------------------------
    private static void CheckSlotWriters(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        foreach (var entry in configuration.State)
        {
            var pointer = Pointer.State(entry.Key);
            var slot = entry.Value;

            if (ReservedStateSlots.Contains(entry.Key))
            {
                errors.Add(Writers(
                    pointer,
                    $"the slot has two writers: '{entry.Key}' is a reserved read-only slot that is always present, and state: declares it again"));
            }

            var owner = OwnerField(slot.Writer);
            if (owner is not null && FieldValue(slot, owner) is null)
            {
                errors.Add(Writers(
                    pointer,
                    $"the slot has zero writers: writer: {WriterName(slot.Writer)} fills the slot from '{owner}:', and the slot declares none"));
            }

            foreach (var field in new[] { "from", "increment", "value" })
            {
                if (string.Equals(field, owner, StringComparison.Ordinal) || FieldValue(slot, field) is null)
                {
                    continue;
                }

                errors.Add(Writers(
                    ConfigurationError.AppendPointer(pointer, field),
                    $"the slot has two writers: writer: {WriterName(slot.Writer)} owns it, and '{field}:' names a second"));
            }
        }
    }

    private static string? OwnerField(StateWriter writer)
        => writer switch
        {
            StateWriter.Tool => "from",
            StateWriter.Counter => "increment",
            StateWriter.Const => "value",
            _ => null,
        };

    private static string WriterName(StateWriter writer)
        => writer switch
        {
            StateWriter.Tool => "tool",
            StateWriter.Counter => "counter",
            StateWriter.Const => "const",
            _ => "extractor",
        };

    private static object? FieldValue(StateSlotConfiguration slot, string field)
        => field switch
        {
            "from" => slot.From,
            "increment" => slot.Increment,
            _ => slot.Value,
        };

    // ---------------------------------------------------------------------------------------------
    // Check 4: guard operators and variables.
    // ---------------------------------------------------------------------------------------------
    private static void CheckGuardRules(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        foreach (var guard in configuration.Guards)
        {
            CheckOneRule(configuration, guard.Value, Pointer.Guard(guard.Key), errors);
        }

        foreach (var slot in configuration.State)
        {
            if (slot.Value.Increment is { } increment)
            {
                CheckOneRule(configuration, increment, ConfigurationError.AppendPointer(Pointer.State(slot.Key), "increment"), errors);
            }
        }

        if (configuration.Policy is { } policy)
        {
            for (var index = 0; index < policy.Stages.Count; index++)
            {
                var stage = policy.Stages[index];
                for (var exit = 0; exit < stage.To.Count; exit++)
                {
                    if (stage.To[exit].When?.Rule is { } rule)
                    {
                        CheckOneRule(configuration, rule, ConfigurationError.AppendPointer(Pointer.Transition(index, exit), "when"), errors);
                    }
                }
            }
        }

        if (configuration.Graph is not { } graph)
        {
            return;
        }

        for (var index = 0; index < graph.Edges.Count; index++)
        {
            if (graph.Edges[index].When?.Rule is { } rule)
            {
                CheckOneRule(configuration, rule, ConfigurationError.AppendPointer(Pointer.Edge(index), "when"), errors);
            }
        }
    }

    private static void CheckOneRule(AgentCoreConfiguration configuration, JsonNode rule, string pointer, List<ConfigurationError> errors)
    {
        var facts = new GuardRuleFacts();
        facts.Collect(rule);

        foreach (var name in facts.Operators)
        {
            if (!GuardOperators.IsAllowed(name))
            {
                errors.Add(Operators(pointer, GuardOperators.DescribeRejection(name)));
            }
        }

        if (facts.HasDoubleNegationSugar)
        {
            errors.Add(Operators(pointer, GuardOperators.DoubleNegationSugarRejection));
        }

        foreach (var slot in facts.Variables)
        {
            if (!configuration.State.ContainsKey(slot) && !ReservedStateSlots.Contains(slot))
            {
                errors.Add(Operators(pointer, $"the rule reads the slot '{slot}', and state: does not declare it"));
            }
        }

        foreach (var comparison in facts.NumericComparisons)
        {
            if (configuration.State.TryGetValue(comparison.Value, out var slot) && slot.Type == StateSlotType.Boolean)
            {
                errors.Add(Operators(
                    pointer,
                    $"the operator '{comparison.Key}' compares the slot '{comparison.Value}', and that slot is a boolean rather than a number"));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 5: exclusivity and coverage by evaluation.
    // ---------------------------------------------------------------------------------------------
    private static void CheckExclusivity(AgentCoreConfiguration configuration, List<ConfigurationError> errors, List<ConfigurationError> warnings)
    {
        var evaluator = new GuardEvaluator(configuration.Guards);

        if (configuration.Policy is { } policy)
        {
            for (var index = 0; index < policy.Stages.Count; index++)
            {
                var stage = policy.Stages[index];
                var exits = new List<SiblingExit>(stage.To.Count);
                for (var exit = 0; exit < stage.To.Count; exit++)
                {
                    var transition = stage.To[exit];
                    var pointer = Pointer.Transition(index, exit);
                    exits.Add(new SiblingExit(
                        DescribeExit(transition.When, "exit", transition.Stage),
                        transition.When is null ? pointer : ConfigurationError.AppendPointer(pointer, "when"),
                        transition.When));
                }

                var pinned = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    // The exits of one stage run inside that stage, so the reserved slot is known.
                    [ReservedStateSlots.Stage] = JsonValue.Create(stage.Id),
                };

                GuardExclusivityCheck.Run(
                    exits,
                    ConfigurationError.AppendPointer(Pointer.Stage(index), "to"),
                    $"the stage '{stage.Id}'",
                    evaluator,
                    configuration.State,
                    pinned,
                    errors,
                    warnings);
            }
        }

        if (configuration.Graph is not { } graph || graph.Edges.Count == 0)
        {
            return;
        }

        foreach (var group in graph.Edges.Select(static (edge, index) => (edge, index)).GroupBy(static pair => pair.edge.From, StringComparer.Ordinal))
        {
            var exits = new List<SiblingExit>();
            foreach (var pair in group)
            {
                var pointer = Pointer.Edge(pair.index);
                exits.Add(new SiblingExit(
                    DescribeExit(pair.edge.When, "edge", pair.edge.To),
                    pair.edge.When is null ? pointer : ConfigurationError.AppendPointer(pointer, "when"),
                    pair.edge.When));
            }

            GuardExclusivityCheck.Run(
                exits,
                "/graph/edges",
                $"the node '{group.Key}'",
                evaluator,
                configuration.State,
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal),
                errors,
                warnings);
        }
    }

    private static string DescribeExit(GuardReference? guard, string kind, string target)
        => guard switch
        {
            null => $"the unconditional {kind} to '{target}'",
            { Name: { } name } => $"the guard '{name}'",
            _ => $"the inline rule on the {kind} to '{target}'",
        };

    // ---------------------------------------------------------------------------------------------
    // Check 6: reachability.
    // ---------------------------------------------------------------------------------------------
    private static void CheckReachability(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        if (configuration.Policy is { } policy)
        {
            var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var stage in policy.Stages)
            {
                edges[stage.Id] = [.. stage.To.Select(static transition => transition.Stage)];
            }

            var reachable = Reach(policy.Initial, edges);

            for (var index = 0; index < policy.Stages.Count; index++)
            {
                var stage = policy.Stages[index];
                if (!reachable.Contains(stage.Id))
                {
                    errors.Add(Reachability(
                        Pointer.Stage(index),
                        $"the stage '{stage.Id}' is unreachable from the initial stage '{policy.Initial}'"));
                }

                if (!stage.Terminal && stage.To.Count == 0)
                {
                    errors.Add(Reachability(
                        Pointer.Stage(index),
                        $"the stage '{stage.Id}' is not terminal and has no exit"));
                }
            }
        }

        if (configuration.Graph is not { } graph || graph.Nodes.Count == 0)
        {
            return;
        }

        var starts = graph.Nodes.Where(static node => node.Start).Select(static node => node.Id).ToList();
        if (starts.Count != 1)
        {
            // Check 7 reports the start-node count. Reachability has no root to walk from.
            return;
        }

        var forward = BuildAdjacency(graph);
        var live = Reach(starts[0], forward);

        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            if (!live.Contains(graph.Nodes[index].Id))
            {
                errors.Add(Reachability(
                    Pointer.Node(index),
                    $"the node '{graph.Nodes[index].Id}' is unreachable from the start node '{starts[0]}'"));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 7: graph well-formedness.
    // ---------------------------------------------------------------------------------------------
    private static void CheckGraphWellFormedness(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        if (configuration.Graph is not { } graph || graph.Nodes.Count == 0)
        {
            return;
        }

        var starts = graph.Nodes.Count(static node => node.Start);
        if (starts != 1)
        {
            errors.Add(WellFormedness(
                "/graph/nodes",
                string.Create(CultureInfo.InvariantCulture, $"the graph declares {starts} start nodes, and check 7 needs exactly one")));
        }

        var outgoing = new HashSet<string>(StringComparer.Ordinal);
        var incoming = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            outgoing.Add(edge.From);
            incoming.Add(edge.To);
        }

        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            var node = graph.Nodes[index];
            if (!outgoing.Contains(node.Id) && !incoming.Contains(node.Id))
            {
                errors.Add(WellFormedness(
                    Pointer.Node(index),
                    $"the node '{node.Id}' is an orphan: no edge reaches it and no edge leaves it"));
            }
        }

        var outputs = graph.Nodes.Where(static node => node.Output).Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (outputs.Count == 0)
        {
            errors.Add(WellFormedness("/graph/nodes", "the graph declares no output node, so no path reaches an output"));
            return;
        }

        var forward = BuildAdjacency(graph);
        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            var node = graph.Nodes[index];
            var reachable = Reach(node.Id, forward);
            if (!reachable.Overlaps(outputs))
            {
                errors.Add(WellFormedness(
                    Pointer.Node(index),
                    $"no path from the node '{node.Id}' reaches an output node"));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Check 8: delegation cycles.
    // ---------------------------------------------------------------------------------------------
    private static void CheckDelegationCycles(AgentCoreConfiguration configuration, List<ConfigurationError> errors)
    {
        var items = configuration.Agents?.Items ?? [];
        if (items.Count == 0)
        {
            return;
        }

        var tools = configuration.Tools.ToDictionary(static tool => tool.Id, static tool => tool, StringComparer.Ordinal);

        // One edge for each agent-as-tool: the agent lists a tool, and that tool declares kind: agent.
        // The 'agent:' field is the one explicit delegation edge in the document. 'uses:' names a
        // built-in and 'binds:' names a host delegate, so neither ever names an agent, and a tool id
        // that matches an agent id is a coincidence.
        var edges = new Dictionary<string, List<(string Target, int Agent, int Slot)>>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            var agent = items[index];
            var outgoing = new List<(string, int, int)>();
            for (var slot = 0; slot < agent.Tools.Count; slot++)
            {
                if (!tools.TryGetValue(agent.Tools[slot], out var tool))
                {
                    continue;
                }

                if (tool.Kind == ToolKind.Agent && tool.Agent is { } target)
                {
                    outgoing.Add((target, index, slot));
                }
            }

            edges[agent.Id] = outgoing;
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var agent in items)
        {
            Visit(agent.Id, edges, state, path, reported, errors);
        }
    }

    private static void Visit(
        string agent,
        Dictionary<string, List<(string Target, int Agent, int Slot)>> edges,
        Dictionary<string, int> state,
        List<string> path,
        HashSet<string> reported,
        List<ConfigurationError> errors)
    {
        if (state.TryGetValue(agent, out var mark) && mark != 0)
        {
            return;
        }

        state[agent] = 1;
        path.Add(agent);

        var outgoing = edges.TryGetValue(agent, out var found) ? found : [];
        foreach (var edge in outgoing)
        {
            var start = path.IndexOf(edge.Target);
            if (start >= 0)
            {
                var cycle = string.Join(" -> ", path.Skip(start).Append(edge.Target));
                if (reported.Add(cycle))
                {
                    errors.Add(new ConfigurationError
                    {
                        Pointer = ConfigurationError.AppendPointer(
                            ConfigurationError.AppendPointer(Pointer.Agent(edge.Agent), "tools"),
                            edge.Slot),
                        // The compiler prints the same chain in the same 'first -> second -> first'
                        // form, and gives the same reason. The two messages agree rather than compete.
                        Message = $"this tool runs the agent '{edge.Target}', and that closes the delegation cycle "
                                  + $"{cycle}. The call would never return.",
                        Check = ConfigurationCheck.DelegationCycles,
                    });
                }

                continue;
            }

            Visit(edge.Target, edges, state, path, reported, errors);
        }

        path.RemoveAt(path.Count - 1);
        state[agent] = 2;
    }

    // ---------------------------------------------------------------------------------------------
    // Shared helpers.
    // ---------------------------------------------------------------------------------------------
    private static Dictionary<string, List<string>> BuildAdjacency(GraphConfiguration graph)
    {
        var forward = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            forward[node.Id] = [];
        }

        foreach (var edge in graph.Edges)
        {
            if (forward.TryGetValue(edge.From, out var targets))
            {
                targets.Add(edge.To);
            }
        }

        return forward;
    }

    private static HashSet<string> Reach(string root, Dictionary<string, List<string>> edges)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { root };
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!edges.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (seen.Add(target))
                {
                    pending.Push(target);
                }
            }
        }

        return seen;
    }

    private static ConfigurationError Reference(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.ReferenceResolution };

    private static ConfigurationError Range(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.ValueRange };

    /// <summary>Refuses a count or an interval that falls outside the range the runtime accepts.</summary>
    /// <param name="value">The configured value.</param>
    /// <param name="min">The lowest accepted value, inclusive.</param>
    /// <param name="max">The highest accepted value, inclusive. <see cref="int.MaxValue"/> means no ceiling.</param>
    /// <param name="pointer">The pointer at the field itself.</param>
    /// <param name="subject">How the message names the field, as a noun phrase.</param>
    /// <param name="why">One or more sentences saying what the range protects.</param>
    /// <param name="errors">Collects the refusal.</param>
    /// <remarks>
    /// Both bounds go through one call so a field cannot be given a floor and left without a ceiling:
    /// every ceiling here stands between a document and a raw throw out of a timer at boot or mid-turn.
    /// </remarks>
    private static void CheckRange(
        int value,
        int min,
        int max,
        string pointer,
        string subject,
        string why,
        List<ConfigurationError> errors)
    {
        if (value >= min && value <= max)
        {
            return;
        }

        var accepted = max == int.MaxValue
            ? $"the lowest accepted value is {min.ToString(CultureInfo.InvariantCulture)}"
            : $"the accepted range is {min.ToString(CultureInfo.InvariantCulture)} to "
                + max.ToString(CultureInfo.InvariantCulture);

        errors.Add(Range(
            pointer,
            $"{subject} is {value.ToString(CultureInfo.InvariantCulture)}, and {accepted}. {why}"));
    }

    private static ConfigurationError Writers(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.SlotWriters };

    private static ConfigurationError Operators(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.GuardOperators };

    private static ConfigurationError Reachability(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.Reachability };

    private static ConfigurationError WellFormedness(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.GraphWellFormedness };

    private sealed class DeclaredNames
    {
        public required HashSet<string> Agents { get; init; }

        public required HashSet<string> Guards { get; init; }

        public required HashSet<string> Stages { get; init; }

        public required HashSet<string> Nodes { get; init; }

        public required HashSet<string> Models { get; init; }

        public static DeclaredNames From(AgentCoreConfiguration configuration)
            => new()
            {
                Agents = (configuration.Agents?.Items ?? [])
                    .Select(static agent => agent.Id).ToHashSet(StringComparer.Ordinal),
                Guards = configuration.Guards.Keys.ToHashSet(StringComparer.Ordinal),
                Stages = (configuration.Policy?.Stages ?? [])
                    .Select(static stage => stage.Id).ToHashSet(StringComparer.Ordinal),
                Nodes = (configuration.Graph?.Nodes ?? [])
                    .Select(static node => node.Id).ToHashSet(StringComparer.Ordinal),

                // An absent providers: section, or an absent providers.llm, declares no model name.
                Models = (configuration.Providers?.Llm ?? [])
                    .Select(static provider => provider.As).ToHashSet(StringComparer.Ordinal),
            };
    }

    private static class Pointer
    {
        public const string Wildcard = "/providers/knowledge/scope/wildcard";

        public const string WildcardValue = Wildcard + "/value";

        public const string WildcardFacets = Wildcard + "/facets";

        public const string FromState = "/providers/knowledge/scope/fromState";

        public const string Ambiguity = "/providers/knowledge/ambiguity";

        public const string Mapper = "/providers/knowledge/mapper";

        public static string State(string slot) => ConfigurationError.AppendPointer("/state", slot);

        public static string Guard(string name) => ConfigurationError.AppendPointer("/guards", name);

        public static string Agent(int index) => ConfigurationError.AppendPointer("/agents/items", index);

        public static string Tool(int index) => ConfigurationError.AppendPointer("/tools", index);

        public static string Mcp(int index) => ConfigurationError.AppendPointer("/mcp", index);

        public static string Stage(int index) => ConfigurationError.AppendPointer("/policy/stages", index);

        public static string Transition(int stage, int exit)
            => ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(Stage(stage), "to"), exit);

        public static string Node(int index) => ConfigurationError.AppendPointer("/graph/nodes", index);

        public static string Edge(int index) => ConfigurationError.AppendPointer("/graph/edges", index);
    }
}
