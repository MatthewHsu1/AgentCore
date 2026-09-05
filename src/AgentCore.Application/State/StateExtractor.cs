using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
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

    private static readonly IReadOnlySet<string> NothingNamed = new HashSet<string>(StringComparer.Ordinal);

    private readonly IChatClient _chatClient;
    private readonly ChatOptions _options;
    private readonly StateValueLinkers _linkers;

    /// <summary>Creates the extractor for one loaded document.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="chatClient">The model named by <c>extractor.model</c>.</param>
    /// <exception cref="InvalidOperationException">The document declares no <c>extractor:</c> section.</exception>
    public StateExtractor(AgentCoreConfiguration configuration, IChatClient chatClient)
        : this(configuration, chatClient, new StateValueLinkers([]))
    {
    }

    /// <summary>Creates the extractor with a linker registry (K12) for its <c>vocabulary:</c> slots.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="chatClient">The model named by <c>extractor.model</c>.</param>
    /// <param name="linkers">The registry <c>StateExtractor.Write</c> resolves each slot's <c>vocabulary.linker</c> against.</param>
    /// <exception cref="InvalidOperationException">The document declares no <c>extractor:</c> section.</exception>
    internal StateExtractor(AgentCoreConfiguration configuration, IChatClient chatClient, StateValueLinkers linkers)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(linkers);

        if (configuration.Extractor is null)
        {
            throw new InvalidOperationException(
                $"The document '{configuration.Name}' declares no extractor, so no extractor slot can be filled.");
        }

        Configuration = configuration;
        _chatClient = chatClient;
        _linkers = linkers;
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
    public Task<StateExtractionResult> ExtractAsync(
        StateDocument state,
        IEnumerable<ChatMessage> transcript,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(transcript);

        return ExtractAsync(state, transcript, new Clarifications(), cancellationToken);
    }

    /// <summary>
    /// Runs one extractor call against the finished turn, then writes what it answered — the linker's
    /// path over a <c>vocabulary:</c> slot's free text included (§6).
    /// </summary>
    /// <param name="state">The state of one call, which carries the vocabulary the linker resolves against.</param>
    /// <param name="transcript">The finished turn, newest last.</param>
    /// <param name="clarifications">The call's ambiguity holder. <see cref="Write(StateDocument,string?,Clarifications)"/> reads and writes it.</param>
    /// <param name="cancellationToken">Cancels the model call.</param>
    /// <returns>What the call did to the state.</returns>
    internal async Task<StateExtractionResult> ExtractAsync(
        StateDocument state,
        IEnumerable<ChatMessage> transcript,
        Clarifications clarifications,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(clarifications);

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

        return Write(state, response.Text, clarifications);
    }

    /// <summary>Writes one extractor reply into the state.</summary>
    /// <param name="state">The state of one call.</param>
    /// <param name="replyText">The raw JSON the model returned.</param>
    /// <returns>What the reply did to the state.</returns>
    public StateExtractionResult Write(StateDocument state, string? replyText)
    {
        ArgumentNullException.ThrowIfNull(state);

        return Write(state, replyText, new Clarifications());
    }

    /// <summary>
    /// Writes one extractor reply into the state, running §6's linker over every <c>vocabulary:</c>
    /// slot's free-text answer first.
    /// </summary>
    /// <param name="state">The state of one call, which carries the vocabulary the linker resolves against.</param>
    /// <param name="replyText">The raw JSON the model returned.</param>
    /// <param name="clarifications">
    /// The call's ambiguity holder (K36). A <c>Linked</c> outcome that writes clears the slot's
    /// pending list and its <c>lastNamed</c> record (K30); an <c>Ambiguous</c> outcome replaces the
    /// pending list, stale or not; a <c>NoMatch</c> outcome touches neither.
    /// </param>
    /// <returns>What the reply did to the state.</returns>
    internal StateExtractionResult Write(
        StateDocument state,
        string? replyText,
        Clarifications clarifications)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clarifications);

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

            var declared = Configuration.State[name];

            // Every answer to a vocabulary: slot goes through the linker, whatever JSON shape it
            // arrived in. The schema asks for a string, but a model can answer 900 for a value
            // spelled "900ENT", and letting that reach the gate directly would coerce it to "900"
            // and skip §6's near-tie check entirely.
            var wrote = declared.Vocabulary is { } vocabularyConfig
                ? WriteLinked(state, name, value, vocabularyConfig, clarifications)
                : WritePlain(state, name, value, clarifications);

            if (wrote)
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

    /// <summary>The non-<c>vocabulary:</c> path: coerce and gate through <see cref="StateDocument.TryWrite"/> as before.</summary>
    private static bool WritePlain(StateDocument state, string slot, JsonNode value, Clarifications clarifications)
    {
        if (!state.TryWrite(slot, value.DeepClone()))
        {
            return false;
        }

        ClearAmbiguity(clarifications, slot);
        return true;
    }

    /// <summary>§6's table: link the mention, then act on the outcome.</summary>
    private bool WriteLinked(
        StateDocument state,
        string slot,
        JsonNode value,
        SlotVocabularyConfiguration vocabularyConfig,
        Clarifications clarifications)
    {
        if (!state.Vocabulary.TryGetValue(slot, out var view))
        {
            // No domain was sampled for this slot this call. The gate would refuse anything
            // anyway, so there is nothing here for the linker to resolve against.
            return false;
        }

        if (!StateValueCoercion.TryCoerce(value, StateSlotType.String, out var coerced)
            || coerced is not JsonValue text
            || !text.TryGetValue<string>(out var mention))
        {
            return false;
        }

        LinkResult result;
        try
        {
            var linker = _linkers.Resolve(vocabularyConfig.Linker);
            result = linker.Link(mention, view, SpokenCandidates(clarifications.Read(slot).LastNamed));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // State extraction must never drop a call. Section 8.7. A host linker is arbitrary
            // code and an unregistered linker name throws from Resolve, so this refuses the one
            // slot rather than letting either unwind the turn.
            return false;
        }

        switch (result.Outcome)
        {
            case LinkOutcome.Linked:
                if (!state.TryWrite(slot, JsonValue.Create(result.Candidates[0])))
                {
                    return false;
                }

                ClearAmbiguity(clarifications, slot);
                return true;

            case LinkOutcome.Ambiguous:
                var candidates = result.Candidates;

                // Overwrites whatever pending list was there, including a stale one channel 1 has
                // already asked about. §7's own recovery path needs this: a caller who near-ties a
                // second, different pair mid-conversation must not be silently dropped because the
                // first pair is still sitting unresolved. Extraction runs after_reply, so this is
                // the caller's own words beating a guess made earlier from search results. K41's
                // fill-only-if-empty guard belongs to the probe, not the linker.
                clarifications.Update(slot, s => s.Pending = candidates);
                return false;

            default:
                return false;
        }
    }

    /// <summary>K30: a successful write clears the slot's pending list and its lastNamed record together.</summary>
    private static void ClearAmbiguity(Clarifications clarifications, string slot)
        => clarifications.Update(slot, s =>
        {
            s.Pending = null;
            s.LastNamed = Clarifications.LastNamed.None;
        });

    private static IReadOnlySet<string> SpokenCandidates(Clarifications.LastNamed lastNamed)
        => lastNamed.Kind == Clarifications.LastNamedKind.Set ? lastNamed.Values! : NothingNamed;

    private static string TypeName(StateSlotType slotType) => slotType switch
    {
        StateSlotType.Boolean => "boolean",
        StateSlotType.Integer => "integer",
        StateSlotType.Number => "number",
        _ => "string",
    };
}
