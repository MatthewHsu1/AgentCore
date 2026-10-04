using Microsoft.Extensions.AI;

namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A model round trip threw. A hook may retry it or answer for it.</summary>
    public sealed class ModelFailureGate : HookGate
    {
        internal ModelFailureGate(
            HookScope scope,
            int round,
            int attempt,
            Exception exception,
            bool canRetry,
            IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ArgumentNullException.ThrowIfNull(items);

            Round = round;
            Attempt = attempt;
            Exception = exception;
            CanRetry = canRetry;
            Items = items;
        }

        /// <summary>Gets the zero-based round trip of this run.</summary>
        public int Round { get; }

        /// <summary>Gets how many retries this round already had.</summary>
        public int Attempt { get; }

        /// <summary>Gets what the round trip threw.</summary>
        public Exception Exception { get; }

        /// <summary>
        /// Gets whether <see cref="Retry"/> is allowed: false once the caller received part of this round, and on the
        /// last attempt the retry cap allows.
        /// </summary>
        public bool CanRetry { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal bool Retrying { get; private set; }

        internal ChatResponse? Response { get; private set; }

        /// <summary>Calls the model again. Allowed only before the first stream update reached the caller. Terminal.</summary>
        /// <exception cref="InvalidOperationException"><see cref="CanRetry"/> is false.</exception>
        public void Retry()
        {
            if (!CanRetry)
            {
                throw new InvalidOperationException("This round cannot be retried: the caller already received part of it, or it reached the retry cap.");
            }

            Stage(terminal: true, () => Retrying = true);
        }

        /// <summary>
        /// Answers the round with this response instead of today's fallback. Terminal. After part of the round reached
        /// the caller, this answer follows it as its own message, as the fallback does.
        /// </summary>
        /// <param name="response">The response the round trip returns.</param>
        public void Respond(ChatResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);
            Stage(terminal: true, () => Response = response);
        }
    }
}
