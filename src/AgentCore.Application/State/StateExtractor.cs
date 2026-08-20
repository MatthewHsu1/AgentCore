using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.State;

/// <summary>
/// Writer 1 of section 8.3, and the bridge from prose to typed state.
/// </summary>
/// <remarks>
/// <para>
/// AgentCore builds one JSON Schema from every <c>writer: extractor</c> slot, all nullable, and asks
/// a model to fill what the turn shows and leave the rest null. The extractor never speaks, holds no
/// tools, and decides nothing. The guards decide.
/// </para>
/// <para>
/// Every slot is nullable on the wire, and that is not optional. A required field that the model
/// omits becomes the default silently, so "unfilled" and "filled false" would be the same state. A
/// <c>null</c> answer means the model did not answer, and it leaves the slot at its previous value.
/// </para>
/// <para>
/// The extractor has no retry. One call runs, and a reply that does not deserialize leaves the slots
/// unchanged. The stage machine stays where it is and the agent tries again next turn.
/// </para>
/// </remarks>
public sealed class StateExtractor
{
    /// <summary>The name the emitted JSON Schema answers to.</summary>
    public const string SchemaName = "agentcore_state";

    private const string SystemPrompt =
        "You read one finished turn of a phone call and report typed state. "
        + "Answer with one JSON object that matches the schema. "
        + "Set a field only when this turn shows the answer. "
        + "Leave every other field null. Null means you did not answer, and it keeps the earlier value. "
        + "Never guess, and never speak to the caller.";

    private readonly IChatClient _chatClient;
    private readonly ChatOptions _options;

    /// <summary>Creates the extractor for one loaded document.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="chatClient">The model named by <c>extractor.model</c>.</param>
    /// <exception cref="InvalidOperationException">The document declares no <c>extractor:</c> section.</exception>
    public StateExtractor(AgentCoreConfiguration configuration, IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(chatClient);

        if (configuration.Extractor is null)
        {
            throw new InvalidOperationException(
                $"The document '{configuration.Name}' declares no extractor, so no extractor slot can be filled.");
        }

        Configuration = configuration;
        _chatClient = chatClient;
        SlotNames = [.. configuration.State
            .Where(entry => entry.Value.Writer == StateWriter.Extractor)
            .Select(entry => entry.Key)];

        Schema = BuildSchema(configuration);
        _options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                Schema,
                SchemaName,
                "The state slots this turn may fill. A null field means the turn did not show the answer."),
            Temperature = (float?)configuration.Extractor.Model.Temperature,
        };
    }

    /// <summary>Gets the loaded document.</summary>
    public AgentCoreConfiguration Configuration { get; }

    /// <summary>Gets the names of the <c>writer: extractor</c> slots, in document order.</summary>
    public IReadOnlyList<string> SlotNames { get; }

    /// <summary>Gets the one JSON Schema the extractor sends. Every property is nullable.</summary>
    public JsonElement Schema { get; }

    /// <summary>Builds the JSON Schema from every <c>writer: extractor</c> slot.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <returns>The schema. Every property is nullable, and every property is required.</returns>
    public static JsonElement BuildSchema(AgentCoreConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        JsonObject properties = [];
        JsonArray required = [];

        foreach (var (name, slot) in configuration.State)
        {
            if (slot.Writer != StateWriter.Extractor)
            {
                continue;
            }

            JsonObject property = new()
            {
                // The null member is the point of this schema. See section 8.3.
                ["type"] = new JsonArray(JsonValue.Create(TypeName(slot.Type)), JsonValue.Create("null")),
            };

            if (slot.Description is not null)
            {
                property["description"] = JsonValue.Create(slot.Description);
            }

            if (slot.EnumValues is { Count: > 0 })
            {
                JsonArray members = [];
                foreach (var member in slot.EnumValues)
                {
                    members.Add(member.DeepClone());
                }

                members.Add(null);
                property["enum"] = members;
            }

            properties[name] = property;
            required.Add(JsonValue.Create(name));
        }

        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Runs one extractor call against the finished turn, then writes what it answered.
    /// </summary>
    /// <param name="state">The state of one call.</param>
    /// <param name="transcript">The finished turn, newest last.</param>
    /// <param name="cancellationToken">Cancels the model call.</param>
    /// <returns>What the call did to the state.</returns>
    public async Task<StateExtractionResult> ExtractAsync(
        StateDocument state,
        IEnumerable<ChatMessage> transcript,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(transcript);

        if (SlotNames.Count == 0)
        {
            return new StateExtractionResult(true, 0, 0, 0, null);
        }

        List<ChatMessage> messages = [new ChatMessage(ChatRole.System, SystemPrompt), .. transcript];

        ChatResponse response;
        try
        {
            response = await _chatClient.GetResponseAsync(messages, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // State extraction must never drop a call. Section 8.7.
            return StateExtractionResult.Failed($"the extractor call failed: {exception.Message}");
        }

        return Write(state, response.Text);
    }

    /// <summary>Writes one extractor reply into the state.</summary>
    /// <param name="state">The state of one call.</param>
    /// <param name="replyText">The raw JSON the model returned.</param>
    /// <returns>What the reply did to the state.</returns>
    public StateExtractionResult Write(StateDocument state, string? replyText)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (string.IsNullOrWhiteSpace(replyText))
        {
            return StateExtractionResult.Failed("the extractor returned an empty reply");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(replyText);
        }
        catch (JsonException exception)
        {
            return StateExtractionResult.Failed($"the extractor reply is not well-formed JSON: {exception.Message}");
        }

        if (parsed is not JsonObject answer)
        {
            return StateExtractionResult.Failed("the extractor reply is not a JSON object");
        }

        int filled = 0, left = 0, rejected = 0;
        foreach (var name in SlotNames)
        {
            if (!answer.TryGetPropertyValue(name, out var value) || value is null)
            {
                // The model did not answer. The slot keeps its earlier value.
                left++;
                continue;
            }

            if (state.TryWrite(name, value.DeepClone()))
            {
                filled++;
            }
            else
            {
                rejected++;
            }
        }

        return new StateExtractionResult(true, filled, left, rejected, null);
    }

    private static string TypeName(StateSlotType slotType) => slotType switch
    {
        StateSlotType.Boolean => "boolean",
        StateSlotType.Integer => "integer",
        StateSlotType.Number => "number",
        _ => "string",
    };
}
