// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/filler_scheduler.py:16-123
// (_FillerScheduler), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; reset_dwell is not ported
// (its only caller, ctx.update() for async tools, is not ported).

using AgentCore.AspNetCore.Voice.Diagnostics;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Threading;

namespace AgentCore.AspNetCore.Voice.Filler
{
    /// <summary>
    /// Fires filler speech after the session has been continuously idle for a dwell, while the
    /// speech this instance was created for is still running.
    /// </summary>
    internal sealed class FillerScheduler : IAsyncDisposable
    {
        private readonly VoiceSession _session;

        private readonly SpeechHandle _speechHandle;

        private readonly FillerSource _source;

        private readonly TimeSpan _delay;

        private readonly TimeSpan? _interval;

        private readonly int? _maxSteps;

        private readonly CancellationTokenSource _cancellation = new();

        private readonly AsyncEvent _speaking = new();

        private readonly List<SpeechHandle> _created = [];

        private readonly Task _mainTask;

        private readonly Lock _gate = new();

        private bool _closing;

        /// <param name="session">Where idleness, state changes, and <c>say</c> come from.</param>
        /// <param name="speechHandle">The loop stops early once this speech is interrupted.</param>
        /// <param name="source">What to say when the dwell elapses.</param>
        /// <param name="delay">How long the session must stay continuously idle before the first fire.</param>
        /// <param name="interval">The wall-clock cooldown after a fire, or <see langword="null"/> to fire at most once.</param>
        /// <param name="maxSteps">The most fires across this instance's lifetime, or <see langword="null"/> for no limit.</param>
        public FillerScheduler(
            VoiceSession session,
            SpeechHandle speechHandle,
            FillerSource source,
            TimeSpan delay,
            TimeSpan? interval = null,
            int? maxSteps = null)
        {
            if (delay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(delay), delay, "delay must be non-negative");
            }

            if (interval is { } value && value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), interval, "interval must be non-negative when set");
            }

            _session = session;
            _speechHandle = speechHandle;
            _source = source;
            _delay = delay;
            _interval = interval;
            _maxSteps = maxSteps;
            _mainTask = Task.Run(RunAsync);
        }

        /// <summary>Gets every speech this instance made <see cref="VoiceSession.Say"/> create, or resolved
        /// straight from a callable source, in fire order.</summary>
        public IReadOnlyList<SpeechHandle> CreatedSpeeches => _created;

        /// <summary>Stops the loop, if it has not already stopped on its own.</summary>
        public async Task CloseAsync()
        {
            lock (_gate)
            {
                _closing = true;
            }

            if (!_mainTask.IsCompleted)
            {
                await TaskTeardown.CancelAndWaitAsync(_cancellation, [_mainTask]).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(CloseAsync());
        }

        private async Task RunAsync()
        {
            void OnAgentState(AgentStateChanged change)
            {
                if (change.NewState is AgentState.Speaking or AgentState.Thinking)
                {
                    _speaking.Set();
                }
            }

            void OnUserState(UserStateChanged change)
            {
                if (change.NewState == UserState.Speaking)
                {
                    _speaking.Set();
                }
            }

            _session.AgentStateChanged += OnAgentState;
            _session.UserStateChanged += OnUserState;

            Task loop = Task.Run(() => LoopAsync(_cancellation.Token), _cancellation.Token);
            try
            {
                await _speechHandle.WaitIfNotInterruptedAsync([loop]).ConfigureAwait(false);
            }
            finally
            {
                if (!loop.IsCompleted)
                {
                    await TaskTeardown.CancelAndWaitAsync(_cancellation, [loop]).ConfigureAwait(false);
                }
                else if (loop.Exception is { } fault)
                {
                    // WaitIfNotInterruptedAsync collects the loop's fault instead of raising it, so a
                    // filler that dies on its first fire would otherwise leave no trace.
                    VoiceConversationLog.FillerFaulted(_session.Logger, fault.GetBaseException());
                }

                _session.AgentStateChanged -= OnAgentState;
                _session.UserStateChanged -= OnUserState;
            }
        }

        private async Task LoopAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await _session.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
                _speaking.Clear();

                try
                {
                    await _speaking.WaitAsync(cancellationToken).WaitAsync(_delay, _session.Time, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (TimeoutException)
                {
                    // The dwell elapsed with no interruption: fire below.
                }

                if (!TryFire())
                {
                    return;
                }

                if (_interval is not { } interval || (_maxSteps is { } max && _created.Count >= max))
                {
                    return;
                }

                await Task.Delay(interval, _session.Time, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <returns><see langword="false"/> when a close began first, so nothing fired.</returns>
        private bool TryFire()
        {
            // No await between resolving the source and using its result: a state change racing this
            // fire must land on the next dwell, not unwind a fire already committed to (filler_scheduler.py:82).
            // The lock keeps a close that lands after the dwell from being passed by this fire, which LiveKit's
            // single-threaded loop gets for free.
            lock (_gate)
            {
                if (_closing)
                {
                    return false;
                }

                object? fired = _source.Resolve(_created.Count);
                SpeechHandle? handle = fired switch
                {
                    string text => _session.Say(text),
                    SpeechHandle already => already,
                    _ => null,
                };

                if (handle is not null)
                {
                    _created.Add(handle);
                }

                return true;
            }
        }
    }
}
