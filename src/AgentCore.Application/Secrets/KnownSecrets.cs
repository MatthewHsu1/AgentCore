namespace AgentCore.Application.Secrets;

/// <summary>
/// Every credential AgentCore itself knows the name of, written down in one place.
/// </summary>
public static class KnownSecrets
{
    /// <summary>The <c>${secret:name}</c> name the OpenAI key resolves under.</summary>
    public const string OpenAiApiKeyName = "openai-api-key";

    /// <summary>The standard OpenAI environment variable, read when the chain holds no name.</summary>
    public const string OpenAiApiKeyVariable = "OPENAI_API_KEY";

    /// <summary>The <c>${secret:name}</c> name the Qdrant key resolves under.</summary>
    public const string QdrantApiKeyName = "qdrant-api-key";

    /// <summary>The standard Qdrant environment variable, read when the chain holds no name.</summary>
    public const string QdrantApiKeyVariable = "QDRANT_API_KEY";

    /// <summary>The <c>${secret:name}</c> name the Grafana Cloud instance id resolves under.</summary>
    public const string GrafanaCloudInstanceIdName = "grafana-cloud-instance-id";

    /// <summary>The environment variable the Grafana Cloud instance id is read from.</summary>
    public const string GrafanaCloudInstanceIdVariable = "GRAFANA_CLOUD_INSTANCE_ID";

    /// <summary>The <c>${secret:name}</c> name the Grafana Cloud token resolves under.</summary>
    public const string GrafanaCloudApiTokenName = "grafana-cloud-api-token";

    /// <summary>The environment variable the Grafana Cloud token is read from.</summary>
    public const string GrafanaCloudApiTokenVariable = "GRAFANA_CLOUD_API_TOKEN";

    /// <summary>The <c>${secret:name}</c> name the PostgreSQL connection string resolves under.</summary>
    public const string PostgresConnectionStringName = "postgres-connection-string";

    /// <summary>The environment variable the PostgreSQL connection string is read from.</summary>
    public const string PostgresConnectionStringVariable = "POSTGRES_CONNECTION_STRING";

    /// <summary>The one OpenAI credential, which chat, embedding, and moderation all read.</summary>
    public static readonly SecretName OpenAi = new(OpenAiApiKeyName, OpenAiApiKeyVariable);

    /// <summary> The Qdrant API key the vector store sends on every call. </summary>
    public static readonly SecretName Qdrant = new(QdrantApiKeyName, QdrantApiKeyVariable);

    /// <summary>The PostgreSQL connection string the audit chain and the transcript are written through.</summary>
    public static readonly SecretName PostgresConnectionString =
        new(PostgresConnectionStringName, PostgresConnectionStringVariable);

    /// <summary>The Grafana Cloud instance id, which is the user half of the OTLP basic credential.</summary>
    public static readonly SecretName GrafanaCloudInstanceId =
        new(GrafanaCloudInstanceIdName, GrafanaCloudInstanceIdVariable);

    /// <summary>The Grafana Cloud token, which is the password half of the OTLP basic credential.</summary>
    public static readonly SecretName GrafanaCloudApiToken =
        new(GrafanaCloudApiTokenName, GrafanaCloudApiTokenVariable);
}
