namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>Numbers the model round trips of one turn, from 0. Nested runs share their parent's counter.</summary>
    internal sealed class TurnRounds
    {
        private int _next = -1;

        internal int Next() => Interlocked.Increment(ref _next);

        /// <summary>Gets how many model round trips the turn has made so far.</summary>
        internal int Count => Volatile.Read(ref _next) + 1;
    }
}
