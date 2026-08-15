namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// One large language model, and the name a <see cref="ModelReference"/> points at.
/// </summary>
public sealed record LlmProviderConfiguration
{
    /// <summary>Gets the vendor, such as <c>openai</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the model name the vendor knows.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the name this entry answers to, such as <c>reply</c> or <c>fill</c>.</summary>
    public required string As { get; init; }
}

/// <summary>
/// A provider that names one vendor and nothing else, such as speech or telephony.
/// </summary>
public sealed record VendorProviderConfiguration
{
    /// <summary>Gets the vendor, such as <c>telnyx-relay</c>.</summary>
    public required string Kind { get; init; }
}

/// <summary>
/// The vendor that carries the call, and the limits of the socket it opens.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>media</b> plane: who carries the call and what route they dial. It is not
/// <c>providers.speech</c>, which is recognition and synthesis, and it is not
/// <c>providers.telephony</c>, which dials, transfers, and hangs up. A warm transfer creates a
/// second media leg, so the control identity in <c>telephony</c> must outlive any one call's
/// socket; this block is per-socket and cannot serve that.
/// </para>
/// <para>
/// The three limits are written in whole seconds and bytes rather than as duration strings. The
/// document carries no other duration, so there was no convention to follow, and integers need no
/// parser and validate with a plain <c>minimum</c>. Each name carries its unit so nothing is
/// guessed. <c>-1</c> on either timeout means <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.
/// </para>
/// </remarks>
public sealed record CallProviderConfiguration
{
    /// <summary>Gets the vendor that carries the call, such as <c>telnyx-relay</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets how long the socket waits with no inbound frame before ending the call, or null for the adapter's default.</summary>
    public int? IdleTimeoutSeconds { get; init; }

    /// <summary>Gets how long teardown gives a stuck task before moving on, or null for the adapter's default.</summary>
    public int? CloseTimeoutSeconds { get; init; }

    /// <summary>Gets the largest inbound frame the socket accepts, in bytes, or null for the adapter's default.</summary>
    public int? MaxFrameBytes { get; init; }
}

/// <summary>
/// The knowledge provider: one adapter for each knowledge port, and what those adapters read.
/// </summary>
/// <remarks>
/// The two ports pick their adapter one at a time, so a document can search a vector store and
/// still read the documents from disk.
/// </remarks>
public sealed record KnowledgeProviderConfiguration
{
    /// <summary>The search adapter used when the document names none.</summary>
    public const string DefaultSearch = "filesystem";

    /// <summary>The document adapter used when the document names none.</summary>
    public const string DefaultDocuments = "filesystem";

    /// <summary>The root used when the document sets none.</summary>
    public const string DefaultRoot = "./kb";

    /// <summary>The collection used when the document names none.</summary>
    public const string DefaultCollection = "kb_chunks";

    /// <summary>Gets the adapter that answers <c>IKnowledgeRetrievalPort</c>, such as <c>zilliz</c>.</summary>
    public string Search { get; init; } = DefaultSearch;

    /// <summary>Gets the adapter that answers <c>IDocumentStorePort</c>, such as <c>filesystem</c>.</summary>
    public string Documents { get; init; } = DefaultDocuments;

    /// <summary>Gets the root of the knowledge-base tree. The tree is its own Git repository.</summary>
    public string Root { get; init; } = DefaultRoot;

    /// <summary>Gets the cluster URL the <c>zilliz</c> adapter reads, or <see langword="null"/>.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets the collection the <c>zilliz</c> adapter reads.</summary>
    public string Collection { get; init; } = DefaultCollection;
}

/// <summary>
/// The telemetry provider: where the signals go, and what is listened to on the way.
/// </summary>
/// <remarks>
/// <para>
/// A document that names none exports nothing. See <c>AgentCore.Application.Ports.ITelemetryAdapter</c>.
/// </para>
/// <para>
/// The two name lists are here rather than inside an adapter because they are not a vendor's
/// business. A deployment decides how much of its own process it pays to watch, and the adapter only
/// carries what it is told to.
/// </para>
/// </remarks>
public sealed record TelemetryProviderConfiguration
{
    /// <summary>The service name used when the document sets none.</summary>
    public const string DefaultServiceName = "agentcore";

    /// <summary>
    /// The metric export interval used when the document sets none, in milliseconds.
    /// </summary>
    /// <remarks>
    /// T61 puts a floor under this, and <see cref="ExportIntervalMilliseconds"/> is where the floor is
    /// enforced. Grafana Cloud bills the greater of active series and data points a minute, so
    /// sending four times a minute costs four times as much as sending once, for the same series.
    /// </remarks>
    public const int DefaultExportIntervalMilliseconds = 60_000;

    /// <summary>The lowest interval T61 allows, in milliseconds.</summary>
    public const int MinimumExportIntervalMilliseconds = 60_000;

    /// <summary>The extra <c>ActivitySource</c> names listened to when the document lists none.</summary>
    public static readonly EquatableList<string> DefaultSources =
        new(["Microsoft.AspNetCore", "System.Net.Http"]);

    /// <summary>The extra <c>Meter</c> names listened to when the document lists none.</summary>
    public static readonly EquatableList<string> DefaultMeters =
        new(["Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http"]);

    /// <summary>Gets the vendor, such as <c>grafana</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the collector URL, or <see langword="null"/> to read it from the environment.</summary>
    /// <remarks>
    /// An endpoint is not a credential, so it belongs in the document. Leaving it out lets the
    /// standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable answer instead, which is what a
    /// deployment that runs a collector beside the process already sets.
    /// </remarks>
    public string? Endpoint { get; init; }

    /// <summary>Gets the <c>service.name</c> every exported signal carries.</summary>
    public string ServiceName { get; init; } = DefaultServiceName;

    /// <summary>Gets how often metrics are sent, in milliseconds, never below the T61 floor.</summary>
    /// <remarks>
    /// A document that writes a smaller number gets <see cref="MinimumExportIntervalMilliseconds"/>
    /// instead. This is a floor and not a validation failure: a number too small costs money quietly
    /// rather than breaking anything, and refusing to start over it would be worse than correcting it.
    /// </remarks>
    public int ExportIntervalMilliseconds
    {
        get => field;
        init => field = Math.Max(value, MinimumExportIntervalMilliseconds);
    } = DefaultExportIntervalMilliseconds;

    /// <summary>Gets the extra <c>ActivitySource</c> names listened to, beside the one AgentCore owns.</summary>
    /// <remarks>
    /// .NET emits these itself, so listening costs a name and no instrumentation package.
    /// <c>Microsoft.AspNetCore</c> is the request span and <c>System.Net.Http</c> is the outbound
    /// call. A document that wants the AgentCore turn span alone writes an empty list.
    /// </remarks>
    public EquatableList<string> Sources { get; init; } = DefaultSources;

    /// <summary>Gets the extra <c>Meter</c> names listened to, beside the one AgentCore owns.</summary>
    /// <remarks>
    /// These are the built-in .NET meters. They are the bulk of the roughly 1,750 active series a
    /// normal ASP.NET Core replica spends against the 10,000 ceiling of item 12, so a deployment that
    /// needs the room takes names off this list.
    /// </remarks>
    public EquatableList<string> Meters { get; init; } = DefaultMeters;
}

/// <summary>
/// The <c>providers:</c> section. It configures adapters and never changes agent shape.
/// </summary>
/// <remarks>
/// This is the seam where the ports in section 7 bind.
/// </remarks>
public sealed record ProvidersConfiguration
{
    /// <summary>Gets the language models, in document order.</summary>
    public EquatableList<LlmProviderConfiguration> Llm { get; init; } = EquatableList<LlmProviderConfiguration>.Empty;

    /// <summary>Gets the vendor that carries the call and owns its inbound route, or <see langword="null"/>.</summary>
    /// <remarks>
    /// The media plane. This is the pipe the audio travels down, and the one seam of the three that
    /// owns an inbound HTTP route.
    /// </remarks>
    public CallProviderConfiguration? Call { get; init; }

    /// <summary>Gets the speech provider, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Recognition and synthesis. Not the pipe — see <see cref="Call"/>.
    /// </remarks>
    public VendorProviderConfiguration? Speech { get; init; }

    /// <summary>Gets the telephony provider, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Dial, transfer, and hang up. Not the pipe — see <see cref="Call"/>. A warm transfer creates a
    /// second media leg, so this identity outlives any one of them.
    /// </remarks>
    public VendorProviderConfiguration? Telephony { get; init; }

    /// <summary>Gets the moderation provider, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <c>AgentCore.Application.Ports.IModerationAdapter</c> reads it, and the host registers one
    /// adapter for each vendor it supports. A document that names none moderates nothing, and every
    /// turn reaches the model.
    /// </remarks>
    public VendorProviderConfiguration? Moderation { get; init; }

    /// <summary>Gets the knowledge provider, or <see langword="null"/>.</summary>
    public KnowledgeProviderConfiguration? Knowledge { get; init; }

    /// <summary>Gets the telemetry provider, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <c>AgentCore.Application.Ports.ITelemetryAdapter</c> reads it, and the host registers one
    /// adapter for each vendor it supports. A document that names none exports nothing, and log lines
    /// go wherever the host already sends them.
    /// </remarks>
    public TelemetryProviderConfiguration? Telemetry { get; init; }
}
