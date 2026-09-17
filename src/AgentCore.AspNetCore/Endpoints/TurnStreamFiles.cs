#pragma warning disable MEAI001 // HostedFileContent is evaluation-only in Microsoft.Extensions.AI 10.10.0.

using System.Runtime.CompilerServices;
using AgentCore.Application.Calls;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// The sandbox files one turn surfaced, linked for the browser once the turn is over.
/// </summary>
/// <remarks>
/// A file reference arrives mid-stream, but its bytes reach the store only after the run, when
/// the capture provider has downloaded them. A link handed out earlier would answer 404. So the
/// references are noted as they pass and linked after the last update, against what the store
/// holds: a file the policy refused, or the download lost, is simply not there and gets no part.
/// </remarks>
internal sealed class TurnStreamFiles
{
    private readonly List<AIContent> _references = [];

    /// <summary>Notes every sandbox file reference one update carries.</summary>
    /// <param name="update">One update of the stream.</param>
    internal void Note(ChatResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            if (content is HostedFileContent or CodeInterpreterToolResultContent)
            {
                _references.Add(content);
            }
        }
    }

    /// <summary>Links every noted file the store kept, in the order they were noted.</summary>
    /// <param name="calls">The door to the stored call.</param>
    /// <param name="callId">The call that owns the files.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>One part for each file the store holds.</returns>
    internal async IAsyncEnumerable<TurnStreamFile> ResolveAsync(
        CallRepository calls,
        string callId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_references.Count == 0)
        {
            yield break;
        }

        var links = await calls
            .LinkFilesAsync(callId, [new ChatMessage(ChatRole.Assistant, _references)], cancellationToken)
            .ConfigureAwait(false);

        foreach (var (blob, url) in links)
        {
            yield return new TurnStreamFile(new FilePayload
            {
                Name = blob.Name,
                MediaType = blob.MediaType,
                Length = blob.Length,
                Url = url?.ToString(),
            });
        }
    }
}
