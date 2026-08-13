using System.Globalization;
using System.Linq.Expressions;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.VectorData;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;

/// <summary>
/// One Zilliz Cloud collection, read over the Milvus v2 REST API.
/// </summary>
/// <remarks>
/// <para>
/// D14 gives this connector the vector store: it holds the cluster, the collection, and the key, and
/// it speaks Milvus. It sits behind <see cref="ZillizRetrievalStore"/>, which is the port the rest of
/// AgentCore sees, so nothing above this class knows the wire format.
/// </para>
/// <para>
/// Only <see cref="SearchAsync"/> is implemented. D14 also assigns the write half of the collection
/// to this connector — <see cref="EnsureCollectionExistsAsync"/>, <see cref="UpsertAsync(ZillizChunkRecord, CancellationToken)"/>,
/// and <see cref="DeleteAsync(string, CancellationToken)"/> — but their only caller is
/// <c>index-sync</c>, and that work is out of scope here. Each one throws
/// <see cref="NotSupportedException"/> rather than opening a half-built write path, and item 3a of
/// section 11 sweeps them so the day one gains a body, the sweep says so.
/// </para>
/// <para>
/// <see cref="GetService"/> is outside that rule and outside the sweep. It is a probe rather than a
/// data plane member, and the package documents a probe as answering <see langword="null"/>, so it
/// answers.
/// </para>
/// <para>
/// This connector holds no embedding generator, so a search takes a vector and never text. The store
/// above it embeds the query. The base class allows either, and the message of the refusal says
/// which one this collection wants.
/// </para>
/// </remarks>
public sealed class ZillizCollection : VectorStoreCollection<string, ZillizChunkRecord>
{
    /// <summary>The Milvus v2 route one search posts to.</summary>
    public const string SearchPath = "/v2/vectordb/entities/search";

    /// <summary>The field of the collection the search ranks by.</summary>
    public const string VectorFieldName = "vector";

    /// <summary>The field of the collection that holds the leaf path of the document.</summary>
    public const string PathFieldName = "path";

    /// <summary>The field of the collection that holds the passage.</summary>
    public const string TextFieldName = "text";

    /// <summary>The vendor name a failure of this connector reports.</summary>
    private const string SystemName = "zilliz";

    private readonly HttpClient _client;
    private readonly string _collection;
    private readonly string _apiKey;

    /// <summary>Opens one collection of one cluster.</summary>
    /// <param name="client">The client. Its <c>BaseAddress</c> is the cluster endpoint.</param>
    /// <param name="collection">The collection this object reads, such as <c>kb_chunks</c>.</param>
    /// <param name="apiKey">The Zilliz key, sent as a bearer token on every request.</param>
    /// <remarks>
    /// Opening costs no request. The first search is the first time this object reaches the cluster,
    /// which is what lets a host start with no network.
    /// </remarks>
    public ZillizCollection(HttpClient client, string collection, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _client = client;
        _collection = collection;
        _apiKey = apiKey;
    }

    /// <summary>Gets the collection this object reads.</summary>
    public override string Name => _collection;

    /// <summary>Ranks the rows of the collection nearest one query vector.</summary>
    /// <typeparam name="TInput">The input type. This connector takes a vector only.</typeparam>
    /// <param name="searchValue">The query vector, as <see cref="ReadOnlyMemory{T}"/> or <c>float[]</c>.</param>
    /// <param name="top">The largest number of rows to return.</param>
    /// <param name="options">
    /// The options. This connector honours none of them, so every option that is not its default is
    /// refused rather than ignored. <see langword="null"/> and an untouched instance both pass.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The rows, in the order the cluster reported them.</returns>
    /// <exception cref="NotSupportedException">
    /// <paramref name="searchValue"/> is not a vector, or <paramref name="options"/> asks for
    /// something this connector does not do.
    /// </exception>
    /// <exception cref="VectorStoreException">The cluster failed, or answered something this connector cannot read.</exception>
    /// <remarks>
    /// The request is made when the result is read and not when this method returns, so the cancel
    /// of the reader is the cancel of the request.
    /// </remarks>
    public override IAsyncEnumerable<VectorSearchResult<ZillizChunkRecord>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        VectorSearchOptions<ZillizChunkRecord>? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);
        Refuse(options);

        // The refusal is decided now rather than on the first read, so a caller that passes text
        // learns it at the call and not somewhere inside a foreach.
        var vector = ToVector(searchValue);
        return SearchCoreAsync(vector, top, cancellationToken);
    }

    /// <summary>Not implemented. <c>index-sync</c> owns the write half of this collection.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(CollectionExistsAsync));

    /// <summary>Not implemented. <c>index-sync</c> owns the write half of this collection.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(EnsureCollectionExistsAsync));

    /// <summary>Not implemented. <c>index-sync</c> owns the write half of this collection.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(EnsureCollectionDeletedAsync));

    /// <summary>Not implemented. <c>knowledge.read</c> opens a document in the file store.</summary>
    /// <param name="key">Unused.</param>
    /// <param name="options">Unused.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task<ZillizChunkRecord?> GetAsync(
        string key,
        RecordRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(GetAsync));

    /// <summary>Not implemented. <c>knowledge.read</c> opens a document in the file store.</summary>
    /// <param name="keys">Unused.</param>
    /// <param name="options">Unused.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override IAsyncEnumerable<ZillizChunkRecord> GetAsync(
        IEnumerable<string> keys,
        RecordRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(GetAsync));

    /// <summary>Not implemented. This connector ranks by vector and does not scan.</summary>
    /// <param name="filter">Unused.</param>
    /// <param name="top">Unused.</param>
    /// <param name="options">Unused.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override IAsyncEnumerable<ZillizChunkRecord> GetAsync(
        Expression<Func<ZillizChunkRecord, bool>> filter,
        int top,
        FilteredRecordRetrievalOptions<ZillizChunkRecord>? options = null,
        CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(GetAsync));

    /// <summary>Not implemented. <c>index-sync</c> owns the write half of this collection.</summary>
    /// <param name="record">Unused.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task UpsertAsync(ZillizChunkRecord record, CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(UpsertAsync));

    /// <summary>Not implemented. <c>index-sync</c> owns the write half of this collection.</summary>
    /// <param name="records">Unused.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task UpsertAsync(IEnumerable<ZillizChunkRecord> records, CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(UpsertAsync));

    /// <summary>Not implemented. <c>index-sync</c> owns the write half of this collection.</summary>
    /// <param name="key">Unused.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(DeleteAsync));

    /// <summary>Not implemented. <c>index-sync</c> owns the write half of this collection.</summary>
    /// <param name="keys">Unused.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override Task DeleteAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
        => throw NotImplementedHere(nameof(DeleteAsync));

    /// <summary>Answers a probe for one object this connector is, or can supply.</summary>
    /// <param name="serviceType">The type asked for.</param>
    /// <param name="serviceKey">The key asked for. A keyed probe finds nothing here.</param>
    /// <returns>This connector when it is of that type, and <see langword="null"/> otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serviceType"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// This is the one member of the base class that D14 does not reach, because it is not a data
    /// plane member: it moves no vector and reads no collection. It is a probe, and the package
    /// documents a probe as answering <see langword="null"/> for a type it does not have. A decorator
    /// that asks for <c>VectorStoreCollectionMetadata</c> must therefore get an answer and not an
    /// exception, so this member keeps the probe contract while every data plane member throws.
    /// </remarks>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>Reads the query vector out of whatever the caller passed.</summary>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <param name="searchValue">The value.</param>
    /// <returns>The vector.</returns>
    /// <exception cref="NotSupportedException">The value is not a vector.</exception>
    private static ReadOnlyMemory<float> ToVector<TInput>(TInput searchValue)
        => searchValue switch
        {
            ReadOnlyMemory<float> vector => vector,
            float[] vector => vector,
            _ => throw new NotSupportedException(
                "ZillizCollection searches by vector. Pass ReadOnlyMemory<float> or float[]: this "
                + "connector holds no embedding generator, and ZillizRetrievalStore embeds the query."),
        };

    /// <summary>Refuses every search option this connector does not honour.</summary>
    /// <param name="options">The options, or <see langword="null"/>.</param>
    /// <exception cref="NotSupportedException">One option is not its default.</exception>
    /// <remarks>
    /// Ignoring an option is worse than refusing it. A caller that filters and gets every row back
    /// reads a wrong answer as a right one, and nothing says so. D14 keeps the unwritten half of this
    /// connector loud for the same reason, so an option it cannot honour is loud too.
    /// </remarks>
    private static void Refuse(VectorSearchOptions<ZillizChunkRecord>? options)
    {
        if (options is null)
        {
            return;
        }

        if (options.Filter is not null)
        {
            throw Unhonoured(nameof(options.Filter), "this connector sends no filter to Milvus");
        }

        if (options.VectorProperty is not null)
        {
            throw Unhonoured(
                nameof(options.VectorProperty),
                "this connector always ranks by the '" + VectorFieldName + "' field");
        }

        if (options.Skip != 0)
        {
            throw Unhonoured(nameof(options.Skip), "this connector reads the first rows and no offset");
        }

        if (options.IncludeVectors)
        {
            throw Unhonoured(
                nameof(options.IncludeVectors),
                "this connector asks for the '" + PathFieldName + "' and '" + TextFieldName
                + "' fields only, so no vector comes back");
        }

        if (options.ScoreThreshold is not null)
        {
            throw Unhonoured(nameof(options.ScoreThreshold), "this connector cuts nothing by distance");
        }
    }

    /// <summary>Builds the refusal one unhonoured search option throws.</summary>
    /// <param name="option">The option the caller set.</param>
    /// <param name="why">What this connector does instead.</param>
    /// <returns>The exception.</returns>
    private static NotSupportedException Unhonoured(string option, string why)
        => new(
            "VectorSearchOptions." + option + " is not honoured by ZillizCollection: " + why
            + ". Leave the option at its default rather than reading an answer that ignored it.");

    /// <summary>Builds the refusal every unimplemented member of this connector throws.</summary>
    /// <param name="member">The member that was called.</param>
    /// <returns>The exception.</returns>
    private static NotSupportedException NotImplementedHere(string member)
        => new(
            "ZillizCollection." + member + " is not implemented. This connector reads the collection "
            + "and nothing else. The members that write it belong to index-sync, and they arrive with it.");

    /// <summary>Ranks by vector, and reads the cluster only while the result is read.</summary>
    /// <param name="vector">The query vector.</param>
    /// <param name="top">The largest number of rows to return.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The rows, in the order the cluster reported them.</returns>
    private async IAsyncEnumerable<VectorSearchResult<ZillizChunkRecord>> SearchCoreAsync(
        ReadOnlyMemory<float> vector,
        int top,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var hit in await ReadHitsAsync(vector, top, cancellationToken).ConfigureAwait(false))
        {
            yield return hit;
        }
    }

    /// <summary>Posts one search and reads every hit out of the answer.</summary>
    /// <param name="vector">The query vector.</param>
    /// <param name="top">The largest number of rows to return.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The rows, in the order the cluster reported them.</returns>
    /// <exception cref="VectorStoreException">The cluster failed, or answered something unreadable.</exception>
    private async Task<List<VectorSearchResult<ZillizChunkRecord>>> ReadHitsAsync(
        ReadOnlyMemory<float> vector,
        int top,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, SearchPath)
        {
            Content = new StringContent(Body(vector, top), Encoding.UTF8, "application/json"),
        };

        // The key is a bearer token on every request. It is never written to a log or a message.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw Failure(
                "answered HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                + " " + response.StatusCode);
        }

        return Read(payload);
    }

    /// <summary>Writes the body the Milvus v2 search route expects.</summary>
    /// <param name="vector">The query vector.</param>
    /// <param name="top">The largest number of rows to return.</param>
    /// <returns>The JSON body.</returns>
    private string Body(ReadOnlyMemory<float> vector, int top)
    {
        JsonArray query = [];
        foreach (var value in vector.Span)
        {
            query.Add(value);
        }

        // One query is one row of the data matrix. The vector itself is never asked back.
        JsonObject body = new()
        {
            ["collectionName"] = _collection,
            ["data"] = new JsonArray(query),
            ["annsField"] = VectorFieldName,
            ["limit"] = top,
            ["outputFields"] = new JsonArray(PathFieldName, TextFieldName),
        };

        return body.ToJsonString();
    }

    /// <summary>Reads the answer of one search.</summary>
    /// <param name="payload">The body the cluster answered.</param>
    /// <returns>The rows, in the order the cluster reported them.</returns>
    /// <exception cref="VectorStoreException">Milvus reported a code, or the body is unreadable.</exception>
    private List<VectorSearchResult<ZillizChunkRecord>> Read(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException error)
        {
            throw Failure("answered a body that is not JSON", error);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Failure("answered a body that is not a JSON object");
            }

            // Milvus reports a logical failure as HTTP 200 with a non-zero code, so the code is read
            // before anything else.
            if (root.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var value)
                && value != 0)
            {
                var said = root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    ? ": " + message.GetString()
                    : string.Empty;

                throw Failure("answered code " + value.ToString(CultureInfo.InvariantCulture) + said);
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw Failure("answered no data array");
            }

            List<VectorSearchResult<ZillizChunkRecord>> hits = new(data.GetArrayLength());
            foreach (var element in data.EnumerateArray())
            {
                var distance = element.TryGetProperty("distance", out var reported)
                    && reported.ValueKind == JsonValueKind.Number
                        ? reported.GetDouble()
                        : 0d;

                ZillizChunkRecord record = new()
                {
                    Path = Field(element, PathFieldName),
                    Text = Field(element, TextFieldName),
                    Distance = distance,
                };

                hits.Add(new VectorSearchResult<ZillizChunkRecord>(record, distance));
            }

            return hits;
        }
    }

    /// <summary>Reads one string field of one hit.</summary>
    /// <param name="hit">The hit.</param>
    /// <param name="field">The field name, which is also an <c>outputFields</c> entry.</param>
    /// <returns>The value.</returns>
    /// <exception cref="VectorStoreException">The hit holds no such field.</exception>
    private string Field(JsonElement hit, string field)
        => hit.ValueKind == JsonValueKind.Object
            && hit.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : throw Failure("answered a hit with no '" + field + "' field");

    /// <summary>Builds the one exception every failure of this connector uses.</summary>
    /// <param name="what">What the cluster did, named for the message.</param>
    /// <param name="cause">The cause, when one exists.</param>
    /// <returns>The exception.</returns>
    /// <remarks>The message names the route, so a failure says which call of Milvus went wrong.</remarks>
    private VectorStoreException Failure(string what, Exception? cause = null)
    {
        var message = "The Zilliz collection '" + _collection + "' failed: POST " + SearchPath + " " + what + ".";

        return new VectorStoreException(message, cause)
        {
            VectorStoreSystemName = SystemName,
            CollectionName = _collection,
            OperationName = nameof(SearchAsync),
        };
    }
}
