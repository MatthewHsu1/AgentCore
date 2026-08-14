namespace AgentCore.Application.Secrets;

/// <summary>
/// Every credential AgentCore itself knows the name of, written down in one place.
/// </summary>
/// <remarks>
/// <para>
/// A vendor adapter used to declare its own two strings, which was fine until a second adapter
/// needed the same key: the Zilliz store embeds every query with OpenAI, and the moderation
/// evaluator checks every turn with OpenAI, so both reached across the assembly into the chat
/// adapter for a name. That coupling is what this catalog removes. An adapter now names the vendor
/// it needs and knows nothing about the adapter that happens to serve the same vendor elsewhere.
/// </para>
/// <para>
/// The raw strings stay <see langword="const"/> so an adapter can forward its own published constant
/// to one of them and publish exactly what it published before. The
/// <see cref="SecretName"/> values beside them are what new code passes to
/// <see cref="SecretResolverExtensions.RequireAsync"/>.
/// </para>
/// <para>
/// This file holds names and never values. A key reaches the process through the resolver chain or
/// through the environment, and never through source.
/// </para>
/// </remarks>
public static class KnownSecrets
{
    /// <summary>The <c>${secret:name}</c> name the OpenAI key resolves under.</summary>
    public const string OpenAiApiKeyName = "openai-api-key";

    /// <summary>The standard OpenAI environment variable, read when the chain holds no name.</summary>
    public const string OpenAiApiKeyVariable = "OPENAI_API_KEY";

    /// <summary>The <c>${secret:name}</c> name the Zilliz key resolves under.</summary>
    public const string ZillizApiKeyName = "zilliz-api-key";

    /// <summary>The standard Zilliz environment variable, read when the chain holds no name.</summary>
    public const string ZillizApiKeyVariable = "ZILLIZ_API_KEY";

    /// <summary>The <c>${secret:name}</c> name the Grafana Cloud instance id resolves under.</summary>
    public const string GrafanaCloudInstanceIdName = "grafana-cloud-instance-id";

    /// <summary>The environment variable the Grafana Cloud instance id is read from.</summary>
    public const string GrafanaCloudInstanceIdVariable = "GRAFANA_CLOUD_INSTANCE_ID";

    /// <summary>The <c>${secret:name}</c> name the Grafana Cloud token resolves under.</summary>
    public const string GrafanaCloudApiTokenName = "grafana-cloud-api-token";

    /// <summary>The environment variable the Grafana Cloud token is read from.</summary>
    public const string GrafanaCloudApiTokenVariable = "GRAFANA_CLOUD_API_TOKEN";

    /// <summary>The one OpenAI credential, which chat, embedding, and moderation all read.</summary>
    /// <remarks>
    /// D13 gives one key to every OpenAI call this host makes, so a host that talks to a model holds
    /// nothing new to embed a query or to moderate a turn.
    /// </remarks>
    public static readonly SecretName OpenAi = new(OpenAiApiKeyName, OpenAiApiKeyVariable);

    /// <summary>The Zilliz Cloud credential the vector store sends on every search.</summary>
    public static readonly SecretName Zilliz = new(ZillizApiKeyName, ZillizApiKeyVariable);

    /// <summary>The Grafana Cloud instance id, which is the user half of the OTLP basic credential.</summary>
    /// <remarks>
    /// <para>
    /// D26 sends every signal to one Grafana Cloud stack, and that stack authenticates one way: HTTP
    /// basic, with the instance id as the user and <see cref="GrafanaCloudApiToken"/> as the password.
    /// The two halves are useless apart, so they are named together here.
    /// </para>
    /// <para>
    /// An instance id is not confidential. It travels through the same resolver anyway, because the
    /// alternative is a second lookup path for one half of one credential, and a deployment that
    /// mounts its Grafana credential as a file would then have to split it.
    /// </para>
    /// <para>
    /// This half is also the switch. A host that resolves no instance id exports with no credential
    /// at all, which is what a collector running beside the process wants. A host that resolves one
    /// must also resolve the token, or it fails at startup rather than sending telemetry that
    /// Grafana answers 401 to and nobody reads.
    /// </para>
    /// </remarks>
    public static readonly SecretName GrafanaCloudInstanceId =
        new(GrafanaCloudInstanceIdName, GrafanaCloudInstanceIdVariable);

    /// <summary>The Grafana Cloud token, which is the password half of the OTLP basic credential.</summary>
    /// <remarks>Required exactly when <see cref="GrafanaCloudInstanceId"/> resolves. See D26.</remarks>
    public static readonly SecretName GrafanaCloudApiToken =
        new(GrafanaCloudApiTokenName, GrafanaCloudApiTokenVariable);
}
