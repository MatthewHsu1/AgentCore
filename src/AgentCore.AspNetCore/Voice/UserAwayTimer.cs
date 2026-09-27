namespace AgentCore.AspNetCore.Voice
{
    /// <summary>The one-shot countdown after which a <see cref="VoiceSession"/> counts the caller as away.</summary>
    /// <param name="time">The clock the countdown runs on.</param>
    /// <param name="options">When and what to say, or <see langword="null"/> to never arm.</param>
    /// <param name="onFire">Runs on a timer thread with the arm that fired. A throw from it ends the process.</param>
    internal sealed class UserAwayTimer(TimeProvider time, UserAwayOptions? options, Action<long> onFire)
    {
        private ITimer? _timer;

        private long _arm;

        /// <summary>Gets when and what to say, or <see langword="null"/> if the timer is disabled.</summary>
        public UserAwayOptions? Options => options;

        /// <summary>Starts a new countdown, replacing any earlier one. Does nothing if disabled.</summary>
        public void Arm()
        {
            Disarm();
            if (options is null)
            {
                return;
            }

            long arm = ++_arm;
            _timer = time.CreateTimer(_ => onFire(arm), null, options.Timeout, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Stops the countdown, if one is running.</summary>
        public void Disarm()
        {
            _timer?.Dispose();
            _timer = null;
        }

        /// <summary>Gets whether <paramref name="arm"/> is the countdown still running.</summary>
        public bool IsCurrent(long arm)
        {
            return _timer is not null && arm == _arm;
        }
    }
}
