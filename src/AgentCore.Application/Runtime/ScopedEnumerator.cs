namespace AgentCore.Application.Runtime;

/// <summary>
/// An enumerator that opens a scope again for every step it takes.
/// </summary>
internal static class ScopedEnumerator
{
    /// <summary>Wraps one enumerator so each step runs under a freshly opened scope.</summary>
    /// <typeparam name="T">What the enumerator yields.</typeparam>
    /// <param name="inner">The enumerator to step.</param>
    /// <param name="enter">
    /// Opens the scope. It is called once per step, and the scope is closed when that step ends.
    /// </param>
    /// <returns>The wrapped enumerator. Disposing it disposes <paramref name="inner"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal static IAsyncEnumerator<T> Over<T>(IAsyncEnumerator<T> inner, Func<IDisposable> enter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(enter);

        return new Stepwise<T>(inner, enter);
    }

    private sealed class Stepwise<T> : IAsyncEnumerator<T>
    {
        private readonly IAsyncEnumerator<T> _inner;

        private readonly Func<IDisposable> _enter;

        public Stepwise(IAsyncEnumerator<T> inner, Func<IDisposable> enter)
        {
            _inner = inner;
            _enter = enter;
        }

        public T Current => _inner.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            using var scope = _enter();

            return await _inner.MoveNextAsync().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
