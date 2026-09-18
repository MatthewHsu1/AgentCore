using System.Text.Json;
using AgentCore.Application.Blobs;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Calls;

/// <summary>
/// Everything a host or a consumer does with a stored call: its row, its words, and the files it
/// published. The one door; the stores behind it are adapters nobody else needs to name.
/// </summary>
public sealed class CallRepository : ICallStore
{
    private readonly ICallStore _calls;

    private readonly IBlobStore? _blobs;

    /// <summary>Makes the repository over the stores the document opened.</summary>
    /// <param name="calls">The store the rows and words live in.</param>
    /// <param name="blobs">The store the published files live in, or <see langword="null"/> when the document names none.</param>
    public CallRepository(ICallStore calls, IBlobStore? blobs)
    {
        ArgumentNullException.ThrowIfNull(calls);

        _calls = calls;
        _blobs = blobs;
    }

    /// <summary>Whether the document named a blob store, so a call can have files at all.</summary>
    public bool KeepsFiles => _blobs is not null;

    /// <summary>The adapter the rows live in. For a host checking which vendor it opened; go through the repository for everything else.</summary>
    public ICallStore Store => _calls;

    /// <summary>Links every published file the messages carry and the store kept, each name once, in first-seen order.</summary>
    /// <param name="callId">The call that owns the files.</param>
    /// <param name="messages">The messages to read the references off: the stored transcript, or one turn's updates.</param>
    /// <param name="cancellationToken">Cancels the signing.</param>
    /// <returns>One link per kept file.</returns>
    public async Task<IReadOnlyList<FileLink>> LinkFilesAsync(
        string callId,
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(messages);

        if (_blobs is null)
        {
            return [];
        }

        List<BlobRef> kept = [];

        foreach (var file in messages.SelectMany(message => message.Contents).OfType<FileContent>())
        {
            if (!file.Kept || !BlobName.IsSafe(file.Name))
            {
                continue;
            }

            BlobRef blob = new(callId, file.Name, file.MediaType, file.Length);
            var at = kept.FindIndex(known => string.Equals(known.Name, blob.Name, StringComparison.Ordinal));

            if (at >= 0)
            {
                kept[at] = blob;
            }
            else
            {
                kept.Add(blob);
            }
        }

        List<FileLink> links = [];

        foreach (var blob in kept)
        {
            var url = await _blobs.LinkAsync(blob, BlobLink.Lifetime, cancellationToken).ConfigureAwait(false);
            links.Add(new FileLink(blob, url));
        }

        return links;
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default)
    {
        if (_blobs is not null)
        {
            await _blobs.DeleteByOwnerAsync(callId, cancellationToken).ConfigureAwait(false);
        }

        await _calls.DeleteAsync(callId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<CallRecord> CreateAsync(string callId, CancellationToken cancellationToken = default)
        => _calls.CreateAsync(callId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<CallRecord?> GetAsync(string callId, CancellationToken cancellationToken = default)
        => _calls.GetAsync(callId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<CallPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        CallStatus? status = null,
        CancellationToken cancellationToken = default)
        => _calls.ListAsync(principalKey, after, limit, status, cancellationToken);

    /// <inheritdoc />
    public ValueTask RenameAsync(string callId, string title, CancellationToken cancellationToken = default)
        => _calls.RenameAsync(callId, title, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetStatusAsync(string callId, CallStatus status, CancellationToken cancellationToken = default)
        => _calls.SetStatusAsync(callId, status, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetCustomAsync(string callId, JsonElement? custom, CancellationToken cancellationToken = default)
        => _calls.SetCustomAsync(callId, custom, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetExternalIdAsync(string callId, string? externalId, CancellationToken cancellationToken = default)
        => _calls.SetExternalIdAsync(callId, externalId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CallMessage>> AppendAsync(
        string callId,
        IReadOnlyList<CallMessageDraft> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
        => _calls.AppendAsync(callId, messages, state, cancellationToken);

    /// <inheritdoc />
    public ValueTask<CallMessage> AppendMessageAsync(string callId, ChatMessage message, CancellationToken cancellationToken = default)
        => _calls.AppendMessageAsync(callId, message, cancellationToken);

    /// <inheritdoc />
    public ValueTask RewriteAsync(string callId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        => _calls.RewriteAsync(callId, messageId, content, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CallMessage>> ReadAsync(string callId, CancellationToken cancellationToken = default)
        => _calls.ReadAsync(callId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> TruncateAsync(string callId, int fromOrdinal, CancellationToken cancellationToken = default)
        => _calls.TruncateAsync(callId, fromOrdinal, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> EraseAsync(string callId, CancellationToken cancellationToken = default)
        => _calls.EraseAsync(callId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> SweepAsync(TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default)
        => _calls.SweepAsync(retention, batchSize, cancellationToken);

    /// <inheritdoc />
    public ValueTask AttachPrincipalAsync(string callId, string principalKey, string role, CancellationToken cancellationToken = default)
        => _calls.AttachPrincipalAsync(callId, principalKey, role, cancellationToken);

    /// <inheritdoc />
    public ValueTask DetachPrincipalAsync(string callId, string principalKey, CancellationToken cancellationToken = default)
        => _calls.DetachPrincipalAsync(callId, principalKey, cancellationToken);

    /// <inheritdoc />
    public ValueTask SaveContinuationAsync(string continuationId, JsonElement envelope, CancellationToken cancellationToken = default)
        => _calls.SaveContinuationAsync(continuationId, envelope, cancellationToken);

    /// <inheritdoc />
    public ValueTask<JsonElement?> GetContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
        => _calls.GetContinuationAsync(continuationId, cancellationToken);

    /// <inheritdoc />
    public ValueTask DeleteContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
        => _calls.DeleteContinuationAsync(continuationId, cancellationToken);
}
