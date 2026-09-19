using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn;

/// <summary>
/// What a turn's tools have produced for the caller and not yet attached to a message, filed under
/// the outermost tool call that produced it.
/// </summary>
/// <remarks>
/// The invoking client opens the outermost tool call around its invocation and takes what was filed
/// under it when it builds the tool-result message. Nested calls keep filing under the outer call;
/// outside any turn there is none, and a publish is discarded.
/// </remarks>
/// <typeparam name="TContent">The content one publish files.</typeparam>
internal abstract class TurnAttachments<TContent> : ITurnAttachments
    where TContent : AIContent
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, List<TContent>> _byCallId = new(StringComparer.Ordinal);

    private string? _outerCallId;

    /// <summary>Gets the id of the outermost tool call now running, or <see langword="null"/> outside one.</summary>
    protected string? OuterCallId => _outerCallId;

    /// <summary>Opens one outermost tool call as the key publishes file under.</summary>
    /// <param name="callId">The id of the outermost tool call now running.</param>
    /// <returns>The scope. Disposing it puts back the key that was open before.</returns>
    public IDisposable BeginOuterCall(string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        string? previous;
        lock (_gate)
        {
            previous = _outerCallId;
            _outerCallId = callId;
        }

        return new OuterCallScope(this, previous);
    }

    /// <summary>Takes what was filed under one outer tool call, in publish order.</summary>
    /// <param name="callId">The call whose content to take.</param>
    /// <returns>What that call filed, or empty.</returns>
    internal IReadOnlyList<TContent> TakeFor(string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_gate)
        {
            return _byCallId.Remove(callId, out var filed) ? filed : [];
        }
    }

    IReadOnlyList<AIContent> ITurnAttachments.TakeFor(string callId) => TakeFor(callId);

    /// <summary>Files one content under a call. A later publish of the same thing wins, in the place the earlier one took, so publish order is kept.</summary>
    /// <param name="callId">The outer call to file under.</param>
    /// <param name="content">What to file.</param>
    /// <param name="sameAs">Whether an already filed content is the same thing as <paramref name="content"/>.</param>
    protected void Attach(string callId, TContent content, Predicate<TContent> sameAs)
    {
        lock (_gate)
        {
            if (!_byCallId.TryGetValue(callId, out var filed))
            {
                filed = [];
                _byCallId[callId] = filed;
            }

            var index = filed.FindIndex(sameAs);
            if (index >= 0)
            {
                filed[index] = content;
            }
            else
            {
                filed.Add(content);
            }
        }
    }

    private sealed class OuterCallScope(TurnAttachments<TContent> owner, string? previous) : IDisposable
    {
        private bool _closed;

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            lock (owner._gate)
            {
                owner._outerCallId = previous;
            }
        }
    }
}
