using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// One <c>apiVersion: agentcore/v1</c> configuration document, bound to records.
/// </summary>
/// <remarks>
/// <para>
/// The parser produces this after check 1 of section 8.5 passes. Checks 2 to 8 read it, and the
/// compile table in section 8.2 turns it into an agent.
/// </para>
/// <para>
/// <b>Do not compare two of these for equality.</b> The collection properties are BCL interfaces,
/// so the compiler-written record equality compares them by reference and two loads of the same
/// document are never equal. Nothing in AgentCore compares two configurations, and rule 17 of
/// section 11 — the same document loads identically as YAML and as JSON — is proved where it is
/// stronger: on the raw document trees, through <see cref="JsonNode.DeepEquals(JsonNode, JsonNode)"/>,
/// which also catches keys these records do not model. See <c>ConfigurationRoundTripTests</c>.
/// </para>
/// </remarks>
public sealed record AgentCoreConfiguration
{
    /// <summary>The only <c>apiVersion</c> value this release accepts.</summary>
    public const string SupportedApiVersion = "agentcore/v1";

    /// <summary>The spoken fallback used when the document names none.</summary>
    /// <remarks>
    /// Section 8.7 asks for a spoken fallback and names no text. This sentence is short, it is
    /// speakable, and it asks the caller to go on, so the call survives the turn that failed.
    /// </remarks>
    public const string DefaultFallbackReply = "I am sorry. I could not finish that. Please say it again.";

    /// <summary>The spoken refusal used when the document names none.</summary>
    /// <remarks>
    /// The sentence is short and it is speakable, and it does not ask the caller to say the request
    /// again. <see cref="DefaultFallbackReply"/> does ask, and after a refusal that invites the
    /// harmful request a second time.
    /// </remarks>
    public const string DefaultRefusalReply = "I am sorry. I cannot help with that request.";

    /// <summary>Gets the document version. It is always <see cref="SupportedApiVersion"/>.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Gets the name of the configured agent.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the line the caller hears when a turn fails.</summary>
    /// <remarks>
    /// Section 8.7 names two failures that end a turn with a spoken fallback: a run that returns no
    /// text after 40 tool rounds, and a tool that fails four times in a row. The key sits at the root
    /// of the document because every shape speaks it: a single agent, a <c>policy:</c> document, and
    /// a <c>graph:</c> document all end a failed turn the same way. It defaults to
    /// <see cref="DefaultFallbackReply"/>, and a document may not set an empty line.
    /// </remarks>
    public string FallbackReply { get; init; } = DefaultFallbackReply;

    /// <summary>Gets the line the caller hears when the agent refuses to answer.</summary>
    /// <remarks>
    /// <para>
    /// Content moderation reads the caller's spoken input before the model runs, and the agent
    /// refuses the turn when the endpoint flags it. The key sits at the root of the document beside
    /// <see cref="FallbackReply"/>, because every shape speaks it: a single agent, a
    /// <c>policy:</c> document, and a <c>graph:</c> document all refuse the same way. It defaults to
    /// <see cref="DefaultRefusalReply"/>, and a document may not set an empty line.
    /// </para>
    /// <para>
    /// <see cref="FallbackReply"/> cannot serve here, for two reasons. First, its default text is
    /// "I am sorry. I could not finish that. Please say it again." Spoken after a refusal, that line
    /// invites the caller to repeat the harmful request. Second, section 8.7 makes
    /// <see cref="FallbackReply"/> the line for a turn that FAILED: a run that returns no text after
    /// 40 tool rounds, or a tool that failed four times. A refusal is not a failure, because the
    /// model was never asked.
    /// </para>
    /// </remarks>
    public string RefusalReply { get; init; } = DefaultRefusalReply;

    /// <summary>Gets the declared state slots, keyed by slot name.</summary>
    public IReadOnlyDictionary<string, StateSlotConfiguration> State { get; init; } = ReadOnlyDictionary<string, StateSlotConfiguration>.Empty;

    /// <summary>Gets the extractor settings, or <see langword="null"/> when the document declares none.</summary>
    public ExtractorConfiguration? Extractor { get; init; }

    /// <summary>Gets the named guards, keyed by guard name. Each value is a raw JSONLogic rule.</summary>
    public IReadOnlyDictionary<string, JsonNode> Guards { get; init; } = ReadOnlyDictionary<string, JsonNode>.Empty;

    /// <summary>Gets the declared tools, in document order.</summary>
    public IReadOnlyList<ToolConfiguration> Tools { get; init; } = [];

    /// <summary>Gets the agent section, or <see langword="null"/> when the document declares none.</summary>
    public AgentsConfiguration? Agents { get; init; }

    /// <summary>Gets the stage machine, or <see langword="null"/> when the document declares none.</summary>
    public PolicyConfiguration? Policy { get; init; }

    /// <summary>Gets the workflow graph, or <see langword="null"/> when the document declares none.</summary>
    public GraphConfiguration? Graph { get; init; }

    /// <summary>Gets the adapter settings, or <see langword="null"/> when the document declares none.</summary>
    public ProvidersConfiguration? Providers { get; init; }

    /// <summary>Gets the evaluation settings, or <see langword="null"/> when the document declares none.</summary>
    public EvaluationConfiguration? Evaluation { get; init; }
}
