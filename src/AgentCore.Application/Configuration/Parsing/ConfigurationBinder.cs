using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// The second stage of the pipeline in section 6: <see cref="JsonNode"/> to records.
/// </summary>
/// <remarks>
/// Check 1 runs before this, so the shape is already known good. Every failure here still carries a
/// JSON Pointer, because a hand-built node tree may reach the binder without the check.
/// </remarks>
internal static class ConfigurationBinder
{
    internal static AgentCoreConfiguration Bind(JsonNode document)
    {
        var root = AsObject(document, ConfigurationError.RootPointer);

        return new AgentCoreConfiguration
        {
            ApiVersion = RequiredString(root, "apiVersion", ConfigurationError.RootPointer),
            Name = RequiredString(root, "name", ConfigurationError.RootPointer),
            FallbackReply = BindFallbackReply(root, ConfigurationError.RootPointer),
            RefusalReply = BindRefusalReply(root, ConfigurationError.RootPointer),
            State = BindState(root, ConfigurationError.RootPointer),
            Extractor = BindExtractor(root, ConfigurationError.RootPointer),
            Guards = BindGuards(root, ConfigurationError.RootPointer),
            Tools = BindTools(root, ConfigurationError.RootPointer),
            Agents = BindAgents(root, ConfigurationError.RootPointer),
            Policy = BindPolicy(root, ConfigurationError.RootPointer),
            Graph = BindGraph(root, ConfigurationError.RootPointer),
            Providers = BindProviders(root, ConfigurationError.RootPointer),
            Evaluation = BindEvaluation(root, ConfigurationError.RootPointer),
        };
    }

    private static string BindFallbackReply(JsonObject root, string pointer)
    {
        if (OptionalString(root, "fallbackReply", pointer) is not { } text)
        {
            return AgentCoreConfiguration.DefaultFallbackReply;
        }

        // Section 8.7 asks for a spoken fallback, and a line with no words is silence on a voice
        // call. That is the failure the fallback exists to prevent, so it is a load error.
        return string.IsNullOrWhiteSpace(text)
            ? throw Error(
                ConfigurationError.AppendPointer(pointer, "fallbackReply"),
                "the fallback reply holds no words. The caller hears it, so it must say something.")
            : text;
    }

    private static string BindRefusalReply(JsonObject root, string pointer)
    {
        if (OptionalString(root, "refusalReply", pointer) is not { } text)
        {
            return AgentCoreConfiguration.DefaultRefusalReply;
        }

        // The agent speaks this line when moderation flags the caller, and a line with no words is
        // silence on a voice call. The caller then hears nothing at all, so it is a load error.
        return string.IsNullOrWhiteSpace(text)
            ? throw Error(
                ConfigurationError.AppendPointer(pointer, "refusalReply"),
                "the refusal reply holds no words. The caller hears it, so it must say something.")
            : text;
    }

    private static EvaluationConfiguration? BindEvaluation(JsonObject root, string pointer)
    {
        if (Property(root, "evaluation") is not { } node)
        {
            return null;
        }

        var evaluationPointer = ConfigurationError.AppendPointer(pointer, "evaluation");
        var evaluation = AsObject(node, evaluationPointer);
        var rate = OptionalDouble(evaluation, "sampleRate", evaluationPointer)
            ?? EvaluationConfiguration.DefaultSampleRate;

        if (double.IsNaN(rate) || rate < 0 || rate > 1)
        {
            throw Error(
                ConfigurationError.AppendPointer(evaluationPointer, "sampleRate"),
                string.Create(CultureInfo.InvariantCulture, $"the sample rate is {rate}, and it runs from 0 through 1"));
        }

        // The judge is optional. A document that names none still runs every evaluator that calls no
        // model, so an absent key is a quieter suite and never a load error.
        var judge = Property(evaluation, "judge") is { } judgeNode
            ? BindModelReference(judgeNode, ConfigurationError.AppendPointer(evaluationPointer, "judge"))
            : null;

        return new EvaluationConfiguration { SampleRate = rate, Judge = judge };
    }

    private static EquatableDictionary<StateSlotConfiguration> BindState(JsonObject root, string pointer)
    {
        if (Property(root, "state") is not { } node)
        {
            return EquatableDictionary<StateSlotConfiguration>.Empty;
        }

        var statePointer = ConfigurationError.AppendPointer(pointer, "state");
        var slots = new List<KeyValuePair<string, StateSlotConfiguration>>();
        foreach (var entry in AsObject(node, statePointer))
        {
            var slotPointer = ConfigurationError.AppendPointer(statePointer, entry.Key);
            var slot = AsObject(Required(entry.Value, slotPointer), slotPointer);

            slots.Add(new KeyValuePair<string, StateSlotConfiguration>(entry.Key, new StateSlotConfiguration
            {
                Type = BindEnum(RequiredString(slot, "type", slotPointer), StateSlotTypes, "type", ConfigurationError.AppendPointer(slotPointer, "type")),
                Writer = BindEnum(RequiredString(slot, "writer", slotPointer), StateWriters, "writer", ConfigurationError.AppendPointer(slotPointer, "writer")),
                Default = Property(slot, "default")?.DeepClone(),
                Description = OptionalString(slot, "description", slotPointer),
                EnumValues = BindEnumValues(slot, slotPointer),
                From = BindFrom(slot, slotPointer),
                Increment = Property(slot, "increment")?.DeepClone(),
                Value = Property(slot, "value")?.DeepClone(),
            }));
        }

        return new EquatableDictionary<StateSlotConfiguration>(slots);
    }

    private static EquatableList<JsonNode>? BindEnumValues(JsonObject slot, string pointer)
    {
        if (Property(slot, "enum") is not { } node)
        {
            return null;
        }

        var enumPointer = ConfigurationError.AppendPointer(pointer, "enum");
        var members = new List<JsonNode>();
        var index = 0;
        foreach (var member in AsArray(node, enumPointer))
        {
            members.Add(Required(member, ConfigurationError.AppendPointer(enumPointer, index)).DeepClone());
            index++;
        }

        return new EquatableList<JsonNode>(members);
    }

    private static ToolResultReference? BindFrom(JsonObject slot, string pointer)
    {
        var text = OptionalString(slot, "from", pointer);
        if (text is null)
        {
            return null;
        }

        if (!ToolResultReference.TryParse(text, out var reference))
        {
            throw Error(
                ConfigurationError.AppendPointer(pointer, "from"),
                $"'{text}' is not a tool result path. The form is toolId.path.");
        }

        return reference;
    }

    private static ExtractorConfiguration? BindExtractor(JsonObject root, string pointer)
    {
        if (Property(root, "extractor") is not { } node)
        {
            return null;
        }

        var extractorPointer = ConfigurationError.AppendPointer(pointer, "extractor");
        var extractor = AsObject(node, extractorPointer);
        var modelPointer = ConfigurationError.AppendPointer(extractorPointer, "model");

        return new ExtractorConfiguration
        {
            Model = BindModelReference(Property(extractor, "model") ?? throw Missing(extractorPointer, "model"), modelPointer),
            When = OptionalString(extractor, "when", extractorPointer) is { } when
                ? BindEnum(when, ExtractorTriggers, "when", ConfigurationError.AppendPointer(extractorPointer, "when"))
                : ExtractorTrigger.AfterReply,
        };
    }

    private static ModelReference BindModelReference(JsonNode node, string pointer)
    {
        var model = AsObject(node, pointer);
        return new ModelReference
        {
            Ref = RequiredString(model, "ref", pointer),
            Temperature = OptionalDouble(model, "temperature", pointer),
        };
    }

    private static EquatableDictionary<JsonNode> BindGuards(JsonObject root, string pointer)
    {
        if (Property(root, "guards") is not { } node)
        {
            return EquatableDictionary<JsonNode>.Empty;
        }

        var guardsPointer = ConfigurationError.AppendPointer(pointer, "guards");
        var guards = new List<KeyValuePair<string, JsonNode>>();
        foreach (var entry in AsObject(node, guardsPointer))
        {
            var guardPointer = ConfigurationError.AppendPointer(guardsPointer, entry.Key);
            guards.Add(new KeyValuePair<string, JsonNode>(entry.Key, Required(entry.Value, guardPointer).DeepClone()));
        }

        return new EquatableDictionary<JsonNode>(guards);
    }

    private static EquatableList<ToolConfiguration> BindTools(JsonObject root, string pointer)
    {
        if (Property(root, "tools") is not { } node)
        {
            return EquatableList<ToolConfiguration>.Empty;
        }

        var toolsPointer = ConfigurationError.AppendPointer(pointer, "tools");
        var tools = new List<ToolConfiguration>();
        var index = 0;
        foreach (var item in AsArray(node, toolsPointer))
        {
            var toolPointer = ConfigurationError.AppendPointer(toolsPointer, index);
            var tool = AsObject(Required(item, toolPointer), toolPointer);

            tools.Add(new ToolConfiguration
            {
                Id = RequiredString(tool, "id", toolPointer),
                Kind = BindEnum(RequiredString(tool, "kind", toolPointer), ToolKinds, "kind", ConfigurationError.AppendPointer(toolPointer, "kind")),
                Description = OptionalString(tool, "description", toolPointer),
                Uses = OptionalString(tool, "uses", toolPointer),
                Binds = OptionalString(tool, "binds", toolPointer),
                Agent = OptionalString(tool, "agent", toolPointer),
                Parameters = Property(tool, "parameters")?.DeepClone(),
                Request = BindHttpRequest(tool, toolPointer),
            });
            index++;
        }

        return new EquatableList<ToolConfiguration>(tools);
    }

    private static HttpRequestConfiguration? BindHttpRequest(JsonObject tool, string pointer)
    {
        if (Property(tool, "request") is not { } node)
        {
            return null;
        }

        var requestPointer = ConfigurationError.AppendPointer(pointer, "request");
        var request = AsObject(node, requestPointer);

        var headers = new List<KeyValuePair<string, SecretTemplate>>();
        if (Property(request, "headers") is { } headersNode)
        {
            var headersPointer = ConfigurationError.AppendPointer(requestPointer, "headers");
            foreach (var entry in AsObject(headersNode, headersPointer))
            {
                var headerPointer = ConfigurationError.AppendPointer(headersPointer, entry.Key);
                var value = AsString(Required(entry.Value, headerPointer), headerPointer);
                headers.Add(new KeyValuePair<string, SecretTemplate>(entry.Key, SecretTemplate.Parse(value)));
            }
        }

        return new HttpRequestConfiguration
        {
            Method = RequiredString(request, "method", requestPointer),
            Url = RequiredString(request, "url", requestPointer),
            Headers = headers.Count == 0
                ? EquatableDictionary<SecretTemplate>.Empty
                : new EquatableDictionary<SecretTemplate>(headers),
        };
    }

    private static AgentsConfiguration? BindAgents(JsonObject root, string pointer)
    {
        if (Property(root, "agents") is not { } node)
        {
            return null;
        }

        var agentsPointer = ConfigurationError.AppendPointer(pointer, "agents");
        var agents = AsObject(node, agentsPointer);

        AgentDefaults? defaults = null;
        if (Property(agents, "defaults") is { } defaultsNode)
        {
            var defaultsPointer = ConfigurationError.AppendPointer(agentsPointer, "defaults");
            var defaultsObject = AsObject(defaultsNode, defaultsPointer);
            defaults = new AgentDefaults
            {
                Model = Property(defaultsObject, "model") is { } modelNode
                    ? BindModelReference(modelNode, ConfigurationError.AppendPointer(defaultsPointer, "model"))
                    : null,
                Instructions = OptionalString(defaultsObject, "instructions", defaultsPointer),
            };
        }

        var itemsPointer = ConfigurationError.AppendPointer(agentsPointer, "items");
        var items = new List<AgentConfiguration>();
        var index = 0;
        foreach (var item in AsArray(Property(agents, "items") ?? throw Missing(agentsPointer, "items"), itemsPointer))
        {
            var itemPointer = ConfigurationError.AppendPointer(itemsPointer, index);
            var agent = AsObject(Required(item, itemPointer), itemPointer);

            items.Add(new AgentConfiguration
            {
                Id = RequiredString(agent, "id", itemPointer),
                Instructions = OptionalString(agent, "instructions", itemPointer),
                Model = Property(agent, "model") is { } agentModel
                    ? BindModelReference(agentModel, ConfigurationError.AppendPointer(itemPointer, "model"))
                    : null,
                Tools = BindStringList(agent, "tools", itemPointer),
            });
            index++;
        }

        return new AgentsConfiguration
        {
            Defaults = defaults,
            Items = new EquatableList<AgentConfiguration>(items),
        };
    }

    private static PolicyConfiguration? BindPolicy(JsonObject root, string pointer)
    {
        if (Property(root, "policy") is not { } node)
        {
            return null;
        }

        var policyPointer = ConfigurationError.AppendPointer(pointer, "policy");
        var policy = AsObject(node, policyPointer);
        var stagesPointer = ConfigurationError.AppendPointer(policyPointer, "stages");

        var stages = new List<StageConfiguration>();
        var index = 0;
        foreach (var item in AsArray(Property(policy, "stages") ?? throw Missing(policyPointer, "stages"), stagesPointer))
        {
            var stagePointer = ConfigurationError.AppendPointer(stagesPointer, index);
            var stage = AsObject(Required(item, stagePointer), stagePointer);

            stages.Add(new StageConfiguration
            {
                Id = RequiredString(stage, "id", stagePointer),
                Agent = OptionalString(stage, "agent", stagePointer),
                Terminal = OptionalBoolean(stage, "terminal", stagePointer) ?? false,
                OnNoMatch = OptionalString(stage, "onNoMatch", stagePointer) is { } onNoMatch
                    ? BindEnum(onNoMatch, StageNoMatches, "onNoMatch", ConfigurationError.AppendPointer(stagePointer, "onNoMatch"))
                    : StageNoMatch.Stay,
                To = BindTransitions(stage, stagePointer),
            });
            index++;
        }

        return new PolicyConfiguration
        {
            Initial = RequiredString(policy, "initial", policyPointer),
            Stages = new EquatableList<StageConfiguration>(stages),
        };
    }

    private static EquatableList<StageTransition> BindTransitions(JsonObject stage, string pointer)
    {
        if (Property(stage, "to") is not { } node)
        {
            return EquatableList<StageTransition>.Empty;
        }

        var toPointer = ConfigurationError.AppendPointer(pointer, "to");
        var transitions = new List<StageTransition>();
        var index = 0;
        foreach (var item in AsArray(node, toPointer))
        {
            var transitionPointer = ConfigurationError.AppendPointer(toPointer, index);
            var transition = AsObject(Required(item, transitionPointer), transitionPointer);

            transitions.Add(new StageTransition
            {
                Stage = RequiredString(transition, "stage", transitionPointer),
                When = BindGuardReference(transition, transitionPointer),
            });
            index++;
        }

        return new EquatableList<StageTransition>(transitions);
    }

    private static GraphConfiguration? BindGraph(JsonObject root, string pointer)
    {
        if (Property(root, "graph") is not { } node)
        {
            return null;
        }

        var graphPointer = ConfigurationError.AppendPointer(pointer, "graph");
        var graph = AsObject(node, graphPointer);

        var nodes = new List<GraphNodeConfiguration>();
        if (Property(graph, "nodes") is { } nodesNode)
        {
            var nodesPointer = ConfigurationError.AppendPointer(graphPointer, "nodes");
            var index = 0;
            foreach (var item in AsArray(nodesNode, nodesPointer))
            {
                var nodePointer = ConfigurationError.AppendPointer(nodesPointer, index);
                var graphNode = AsObject(Required(item, nodePointer), nodePointer);

                nodes.Add(new GraphNodeConfiguration
                {
                    Id = RequiredString(graphNode, "id", nodePointer),
                    Agent = OptionalString(graphNode, "agent", nodePointer),
                    Start = OptionalBoolean(graphNode, "start", nodePointer) ?? false,
                    Output = OptionalBoolean(graphNode, "output", nodePointer) ?? false,
                });
                index++;
            }
        }

        var edges = new List<GraphEdgeConfiguration>();
        if (Property(graph, "edges") is { } edgesNode)
        {
            var edgesPointer = ConfigurationError.AppendPointer(graphPointer, "edges");
            var index = 0;
            foreach (var item in AsArray(edgesNode, edgesPointer))
            {
                var edgePointer = ConfigurationError.AppendPointer(edgesPointer, index);
                var edge = AsObject(Required(item, edgePointer), edgePointer);

                edges.Add(new GraphEdgeConfiguration
                {
                    From = RequiredString(edge, "from", edgePointer),
                    To = RequiredString(edge, "to", edgePointer),
                    When = BindGuardReference(edge, edgePointer),
                });
                index++;
            }
        }

        return new GraphConfiguration
        {
            Pattern = OptionalString(graph, "pattern", graphPointer) is { } pattern
                ? BindEnum(pattern, GraphPatterns, "pattern", ConfigurationError.AppendPointer(graphPointer, "pattern"))
                : null,
            Agents = BindStringList(graph, "agents", graphPointer),
            Nodes = nodes.Count == 0 ? EquatableList<GraphNodeConfiguration>.Empty : new EquatableList<GraphNodeConfiguration>(nodes),
            Edges = edges.Count == 0 ? EquatableList<GraphEdgeConfiguration>.Empty : new EquatableList<GraphEdgeConfiguration>(edges),
        };
    }

    private static ProvidersConfiguration? BindProviders(JsonObject root, string pointer)
    {
        if (Property(root, "providers") is not { } node)
        {
            return null;
        }

        var providersPointer = ConfigurationError.AppendPointer(pointer, "providers");
        var providers = AsObject(node, providersPointer);

        var llm = new List<LlmProviderConfiguration>();
        if (Property(providers, "llm") is { } llmNode)
        {
            var llmPointer = ConfigurationError.AppendPointer(providersPointer, "llm");
            var index = 0;
            foreach (var item in AsArray(llmNode, llmPointer))
            {
                var itemPointer = ConfigurationError.AppendPointer(llmPointer, index);
                var provider = AsObject(Required(item, itemPointer), itemPointer);

                llm.Add(new LlmProviderConfiguration
                {
                    Kind = RequiredString(provider, "kind", itemPointer),
                    Model = RequiredString(provider, "model", itemPointer),
                    As = RequiredString(provider, "as", itemPointer),
                });
                index++;
            }
        }

        return new ProvidersConfiguration
        {
            Llm = llm.Count == 0 ? EquatableList<LlmProviderConfiguration>.Empty : new EquatableList<LlmProviderConfiguration>(llm),
            Speech = BindVendorProvider(providers, "speech", providersPointer),
            Telephony = BindVendorProvider(providers, "telephony", providersPointer),
            Moderation = BindVendorProvider(providers, "moderation", providersPointer),
            Knowledge = BindKnowledgeProvider(providers, providersPointer),
        };
    }

    private static VendorProviderConfiguration? BindVendorProvider(JsonObject providers, string name, string pointer)
    {
        if (Property(providers, name) is not { } node)
        {
            return null;
        }

        var vendorPointer = ConfigurationError.AppendPointer(pointer, name);
        return new VendorProviderConfiguration
        {
            Kind = RequiredString(AsObject(node, vendorPointer), "kind", vendorPointer),
        };
    }

    private static KnowledgeProviderConfiguration? BindKnowledgeProvider(JsonObject providers, string pointer)
    {
        if (Property(providers, "knowledge") is not { } node)
        {
            return null;
        }

        var knowledgePointer = ConfigurationError.AppendPointer(pointer, "knowledge");
        var knowledge = AsObject(node, knowledgePointer);

        return new KnowledgeProviderConfiguration
        {
            Search = OptionalString(knowledge, "search", knowledgePointer) ?? KnowledgeProviderConfiguration.DefaultSearch,
            Documents = OptionalString(knowledge, "documents", knowledgePointer) ?? KnowledgeProviderConfiguration.DefaultDocuments,
            Root = OptionalString(knowledge, "root", knowledgePointer) ?? KnowledgeProviderConfiguration.DefaultRoot,
            Endpoint = OptionalString(knowledge, "endpoint", knowledgePointer),
            Collection = OptionalString(knowledge, "collection", knowledgePointer) ?? KnowledgeProviderConfiguration.DefaultCollection,
        };
    }

    private static GuardReference? BindGuardReference(JsonObject parent, string pointer)
    {
        if (Property(parent, "when") is not { } node)
        {
            return null;
        }

        var whenPointer = ConfigurationError.AppendPointer(pointer, "when");
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return GuardReference.FromName(value.GetValue<string>());
        }

        return GuardReference.FromRule(node.DeepClone());
    }

    private static EquatableList<string> BindStringList(JsonObject parent, string name, string pointer)
    {
        if (Property(parent, name) is not { } node)
        {
            return EquatableList<string>.Empty;
        }

        var listPointer = ConfigurationError.AppendPointer(pointer, name);
        var values = new List<string>();
        var index = 0;
        foreach (var item in AsArray(node, listPointer))
        {
            var itemPointer = ConfigurationError.AppendPointer(listPointer, index);
            values.Add(AsString(Required(item, itemPointer), itemPointer));
            index++;
        }

        return values.Count == 0 ? EquatableList<string>.Empty : new EquatableList<string>(values);
    }

    private static readonly Dictionary<string, StateSlotType> StateSlotTypes = new(StringComparer.Ordinal)
    {
        ["boolean"] = StateSlotType.Boolean,
        ["integer"] = StateSlotType.Integer,
        ["number"] = StateSlotType.Number,
        ["string"] = StateSlotType.String,
    };

    private static readonly Dictionary<string, StateWriter> StateWriters = new(StringComparer.Ordinal)
    {
        ["extractor"] = StateWriter.Extractor,
        ["tool"] = StateWriter.Tool,
        ["counter"] = StateWriter.Counter,
        ["const"] = StateWriter.Const,
    };

    private static readonly Dictionary<string, ExtractorTrigger> ExtractorTriggers = new(StringComparer.Ordinal)
    {
        ["after_reply"] = ExtractorTrigger.AfterReply,
    };

    private static readonly Dictionary<string, ToolKind> ToolKinds = new(StringComparer.Ordinal)
    {
        ["builtin"] = ToolKind.Builtin,
        ["http"] = ToolKind.Http,
        ["binding"] = ToolKind.Binding,
        ["agent"] = ToolKind.Agent,
    };

    private static readonly Dictionary<string, StageNoMatch> StageNoMatches = new(StringComparer.Ordinal)
    {
        ["stay"] = StageNoMatch.Stay,
        ["error"] = StageNoMatch.Error,
    };

    private static readonly Dictionary<string, GraphPattern> GraphPatterns = new(StringComparer.Ordinal)
    {
        ["sequential"] = GraphPattern.Sequential,
        ["concurrent"] = GraphPattern.Concurrent,
        ["handoff"] = GraphPattern.Handoff,
        ["group_chat"] = GraphPattern.GroupChat,
    };

    private static TEnum BindEnum<TEnum>(string text, Dictionary<string, TEnum> map, string name, string pointer)
        where TEnum : struct, Enum
    {
        if (map.TryGetValue(text, out var value))
        {
            return value;
        }

        throw Error(pointer, $"'{text}' is not a {name}. The values are: {string.Join(", ", map.Keys)}.");
    }

    private static JsonNode? Property(JsonObject parent, string name)
        => parent.TryGetPropertyValue(name, out var value) ? value : null;

    private static JsonNode Required(JsonNode? node, string pointer)
        => node ?? throw Error(pointer, "a value is expected, and the document holds null");

    private static JsonObject AsObject(JsonNode node, string pointer)
        => node as JsonObject ?? throw Error(pointer, "an object is expected");

    private static JsonArray AsArray(JsonNode node, string pointer)
        => node as JsonArray ?? throw Error(pointer, "an array is expected");

    private static string AsString(JsonNode node, string pointer)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw Error(pointer, "a string is expected");

    private static string RequiredString(JsonObject parent, string name, string pointer)
    {
        var node = Property(parent, name) ?? throw Missing(pointer, name);
        return AsString(node, ConfigurationError.AppendPointer(pointer, name));
    }

    private static string? OptionalString(JsonObject parent, string name, string pointer)
        => Property(parent, name) is { } node
            ? AsString(node, ConfigurationError.AppendPointer(pointer, name))
            : null;

    private static bool? OptionalBoolean(JsonObject parent, string name, string pointer)
    {
        if (Property(parent, name) is not { } node)
        {
            return null;
        }

        var valuePointer = ConfigurationError.AppendPointer(pointer, name);
        return node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : throw Error(valuePointer, "a boolean is expected");
    }

    private static double? OptionalDouble(JsonObject parent, string name, string pointer)
    {
        if (Property(parent, name) is not { } node)
        {
            return null;
        }

        var valuePointer = ConfigurationError.AppendPointer(pointer, name);
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            ? value.GetValue<double>()
            : throw Error(valuePointer, "a number is expected");
    }

    private static ConfigurationLoadException Missing(string pointer, string name)
        => Error(pointer, string.Create(CultureInfo.InvariantCulture, $"the required property '{name}' is missing"));

    private static ConfigurationLoadException Error(string pointer, string message)
        => new(new ConfigurationError
        {
            Pointer = pointer,
            Message = message,
            Check = ConfigurationCheck.DocumentSchema,
        });
}
