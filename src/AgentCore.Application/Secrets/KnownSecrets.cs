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

    /// <summary>The one OpenAI credential, which chat, embedding, and moderation all read.</summary>
    /// <remarks>
    /// D13 gives one key to every OpenAI call this host makes, so a host that talks to a model holds
    /// nothing new to embed a query or to moderate a turn.
    /// </remarks>
    public static readonly SecretName OpenAi = new(OpenAiApiKeyName, OpenAiApiKeyVariable);

    /// <summary>The Zilliz Cloud credential the vector store sends on every search.</summary>
    public static readonly SecretName Zilliz = new(ZillizApiKeyName, ZillizApiKeyVariable);
}
