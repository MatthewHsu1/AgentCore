using AgentCore.Application.Transcript;
using AgentCore.Application.Conversation;
using Microsoft.Extensions.AI;
using AgentCore.Application.Blobs;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The files one turn published, listed for the browser once the turn is over.
    /// </summary>
    internal sealed class TurnStreamFiles
    {
        private readonly List<FileContent> _files = [];

        /// <summary>Notes every published file one update carries.</summary>
        /// <param name="update">One update of the stream.</param>
        internal void Note(ChatResponseUpdate update)
        {
            _files.AddRange(update.Contents.OfType<FileContent>());
        }

        /// <summary>Lists every noted file the store kept, in the order they were noted.</summary>
        /// <param name="conversations">The door to the stored conversation.</param>
        /// <param name="conversationId">The conversation that owns the files.</param>
        /// <returns>One part for each file the store holds.</returns>
        internal IEnumerable<TurnStreamPart> Resolve(Conversations conversations, string conversationId)
        {
            if (_files.Count == 0)
            {
                yield break;
            }

            foreach (BlobRef blob in conversations.KeptFiles(conversationId, [new ChatMessage(ChatRole.Assistant, [.. _files])]))
            {
                yield return new TurnStreamPart(TurnStreamPart.File, new FilePayload
                {
                    Name = blob.Name,
                    Title = _files.LastOrDefault(file => string.Equals(file.Name, blob.Name, StringComparison.Ordinal))?.Title,
                    MediaType = blob.MediaType,
                    Length = blob.Length,
                });
            }
        }
    }
}
