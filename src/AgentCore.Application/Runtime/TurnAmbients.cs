using AgentCore.Application.Ports;
using AgentCore.Application.State;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The four ambients a turn runs under, opened and closed as one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CallStateScope"/>, <see cref="CallRenderScope"/>, <see cref="ToolFailureScope"/> and
/// <see cref="TurnContextScope"/> are separate seams, but no turn wants one without the others.
/// <see cref="CallSession"/> opens them at three sites, so bundling them here is what keeps a fifth
/// ambient a one-line change and stops one site from silently running with an ambient the other two
/// have.
/// </para>
/// <para>
/// An async iterator restores the execution context of its caller at every <c>yield return</c>, so
/// no ambient survives one. A streaming turn opens this again for every round, and that re-entry is
/// what <c>CallRenderScopeTests.ATurnThatStreams_ShowsItsToolsTheScreenToo</c> holds.
/// </para>
/// </remarks>
internal static class TurnAmbients
{
    /// <summary>Opens all four over this flow of execution.</summary>
    /// <param name="state">The state document the call reads and writes.</param>
    /// <param name="screen">The screen this call draws on, or <see langword="null"/> when it has none.</param>
    /// <param name="onToolFailure">What to do with a tool failure the run reports.</param>
    /// <param name="context">The turn the tools are running inside.</param>
    /// <returns>The scope. Disposing it closes all four, newest first.</returns>
    internal static IDisposable Enter(
        StateDocument state,
        IRenderPort? screen,
        Action<ToolFailure> onToolFailure,
        TurnContext context)
        => new Scope(
            CallStateScope.Enter(state),
            CallRenderScope.Enter(screen),
            ToolFailureScope.Enter(onToolFailure),
            TurnContextScope.Enter(context));

    /// <summary>One open scope over all four ambients.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly IDisposable _state;
        private readonly IDisposable _screen;
        private readonly IDisposable _faults;
        private readonly IDisposable _context;

        public Scope(IDisposable state, IDisposable screen, IDisposable faults, IDisposable context)
        {
            _state = state;
            _screen = screen;
            _faults = faults;
            _context = context;
        }

        /// <remarks>
        /// Newest first, the order <c>using</c> would have closed them in. Each of the four already
        /// refuses a second dispose, so this one needs no guard of its own.
        /// </remarks>
        public void Dispose()
        {
            _context.Dispose();
            _faults.Dispose();
            _screen.Dispose();
            _state.Dispose();
        }
    }
}
