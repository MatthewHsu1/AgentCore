namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// Starts an open that will stall in <see cref="GatedSessionFactory"/> on a thread of its own.
    /// </summary>
    internal static class OwnThread
    {
        /// <summary>Runs <paramref name="open"/> on a dedicated thread.</summary>
        /// <typeparam name="T">The result of the open.</typeparam>
        /// <param name="open">The open to run.</param>
        /// <param name="cancellationToken">Cancels the start.</param>
        /// <returns>The task of the open.</returns>
        internal static Task<T> Run<T>(Func<Task<T>> open, CancellationToken cancellationToken) =>
            Task.Factory.StartNew(open, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }
}
