using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using static AgentCore.Application.Configuration.Validation.ValidationErrors;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>
    /// Check 2, reference resolution: every agent, model, guard, stage, and node a document names is
    /// one it declares. Tool ids are the other half, in <see cref="ServedReferenceCheck"/>.
    /// </summary>
    internal static class ReferenceCheck
    {
        public static void Run(AgentCoreConfiguration configuration, DeclaredNames names, List<ConfigurationError> errors)
        {
            CheckAgentTools(configuration.Tools, names, errors);
            CheckTopLevelModels(configuration, names, errors);
            CheckAgents(configuration.Agents, names, errors);

            foreach ((string? name, EntryConfiguration? entry) in configuration.Entries)
            {
                if (entry.Policy is { } policy)
                {
                    CheckPolicy(name, policy, names, errors);
                }

                if (entry.Graph is { } graph)
                {
                    CheckGraph(name, graph, names, errors);
                }
            }
        }

        // A kind: agent tool names the agent it runs, and that name resolves whether or not any agent
        // lists the tool. The compiler catches only the listed case, and only after the load passes.
        private static void CheckAgentTools(IReadOnlyList<ToolConfiguration> tools, DeclaredNames names, List<ConfigurationError> errors)
        {
            for (int index = 0; index < tools.Count; index++)
            {
                ToolConfiguration tool = tools[index];
                if (tool.Kind == ToolKind.Agent && tool.Agent is { } target)
                {
                    AddUnknownAgent(target, ConfigurationError.AppendPointer(ValidationPointer.Tool(index), ValidationPointer.AgentField), names, errors);
                }
            }
        }

        private static void CheckTopLevelModels(AgentCoreConfiguration configuration, DeclaredNames names, List<ConfigurationError> errors)
        {
            AddUnknownModel(configuration.Extractor?.Model, "/extractor/model/ref", names, errors);
            AddUnknownModel(configuration.Evaluation?.Judge, "/evaluation/judge/ref", names, errors);
            AddUnknownModel(configuration.Titler?.Model, "/titler/model/ref", names, errors);
            AddUnknownModel(configuration.Agents.Defaults?.Model, "/agents/defaults/model/ref", names, errors);
        }

        private static void CheckAgents(AgentsConfiguration agents, DeclaredNames names, List<ConfigurationError> errors)
        {
            IReadOnlyList<AgentConfiguration> items = agents.Items;
            for (int index = 0; index < items.Count; index++)
            {
                AgentConfiguration agent = items[index];
                string pointer = ValidationPointer.Agent(index);

                AddUnknownModel(
                    agent.Model,
                    ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(pointer, "model"), "ref"),
                    names,
                    errors);

                for (int child = 0; child < agent.Background.Count; child++)
                {
                    AddUnknownAgent(
                        agent.Background[child],
                        ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(pointer, "background"), child),
                        names,
                        errors);
                }
            }
        }

        private static void CheckPolicy(string name, PolicyConfiguration policy, DeclaredNames names, List<ConfigurationError> errors)
        {
            HashSet<string> stages = names.Stages[name];

            AddUnknownStage(name, policy.Initial, ConfigurationError.AppendPointer(ValidationPointer.Policy(name), "initial"), stages, errors);

            for (int index = 0; index < policy.Stages.Count; index++)
            {
                StageConfiguration stage = policy.Stages[index];

                if (stage.Agent is { } agentId)
                {
                    AddUnknownAgent(agentId, ConfigurationError.AppendPointer(ValidationPointer.Stage(name, index), ValidationPointer.AgentField), names, errors);
                }

                for (int exit = 0; exit < stage.To.Count; exit++)
                {
                    StageTransition transition = stage.To[exit];
                    string pointer = ValidationPointer.Transition(name, index, exit);

                    AddUnknownStage(name, transition.Stage, ConfigurationError.AppendPointer(pointer, "stage"), stages, errors);
                    AddUnknownGuard(transition.When, ConfigurationError.AppendPointer(pointer, "when"), names, errors);
                }
            }
        }

        private static void CheckGraph(string name, GraphConfiguration graph, DeclaredNames names, List<ConfigurationError> errors)
        {
            HashSet<string> nodes = names.Nodes[name];

            for (int index = 0; index < graph.Agents.Count; index++)
            {
                AddUnknownAgent(graph.Agents[index], ConfigurationError.AppendPointer(ValidationPointer.GraphAgents(name), index), names, errors);
            }

            for (int index = 0; index < graph.Nodes.Count; index++)
            {
                if (graph.Nodes[index].Agent is { } nodeAgent)
                {
                    AddUnknownAgent(nodeAgent, ConfigurationError.AppendPointer(ValidationPointer.Node(name, index), ValidationPointer.AgentField), names, errors);
                }
            }

            for (int index = 0; index < graph.Edges.Count; index++)
            {
                GraphEdgeConfiguration edge = graph.Edges[index];
                string pointer = ValidationPointer.Edge(name, index);

                AddUnknownNode(name, edge.From, ConfigurationError.AppendPointer(pointer, "from"), nodes, errors);
                AddUnknownNode(name, edge.To, ConfigurationError.AppendPointer(pointer, "to"), nodes, errors);
                AddUnknownGuard(edge.When, ConfigurationError.AppendPointer(pointer, "when"), names, errors);
            }
        }

        private static void AddUnknownAgent(string agentId, string pointer, DeclaredNames names, List<ConfigurationError> errors)
        {
            if (!names.Agents.Contains(agentId))
            {
                errors.Add(Reference(pointer, $"the agent '{agentId}' is not declared in agents.items"));
            }
        }

        private static void AddUnknownStage(string entry, string stage, string pointer, HashSet<string> stages, List<ConfigurationError> errors)
        {
            if (!stages.Contains(stage))
            {
                errors.Add(Reference(pointer, $"the stage '{stage}' is not declared in policy.stages in entry '{entry}'"));
            }
        }

        private static void AddUnknownNode(string entry, string node, string pointer, HashSet<string> nodes, List<ConfigurationError> errors)
        {
            if (!nodes.Contains(node))
            {
                errors.Add(Reference(pointer, $"the node '{node}' is not declared in graph.nodes in entry '{entry}'"));
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
        private static void AddUnknownModel(ModelReference? model, string pointer, DeclaredNames names, List<ConfigurationError> errors)
        {
            if (model is { } reference && !names.Models.Contains(reference.Ref))
            {
                errors.Add(Reference(pointer, $"the model '{reference.Ref}' is not declared in providers.llm"));
            }
        }
    }
}
