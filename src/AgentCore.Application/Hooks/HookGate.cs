namespace AgentCore.Application.Hooks
{
    /// <summary>
    /// One hook's view of a gate. It shows the facts as the hooks before this one left them, and takes this hook's
    /// verbs. A terminal verb ends the chain; a modifying verb changes what the next hook sees.
    /// </summary>
    public abstract class HookGate
    {
        private readonly Lock _verbs = new();

        private bool _sealed;

        private bool _terminal;

        private protected HookGate(HookScope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);
            Scope = scope;
        }

        /// <summary>Gets where and when the gate fires.</summary>
        public HookScope Scope { get; }

        /// <summary>Gets whether this view took a terminal verb.</summary>
        internal bool IsTerminal
        {
            get
            {
                lock (_verbs)
                {
                    return _terminal;
                }
            }
        }

        /// <summary>Closes this view. Every verb after this throws.</summary>
        internal void Seal()
        {
            lock (_verbs)
            {
                _sealed = true;
            }
        }

        /// <summary>Records one verb on this view.</summary>
        /// <param name="terminal">Whether the verb ends the chain.</param>
        /// <param name="apply">Writes the verb's value into the view's staged fields.</param>
        /// <exception cref="InvalidOperationException">The view is sealed, or it already took a terminal verb.</exception>
        private protected void Stage(bool terminal, Action apply)
        {
            ArgumentNullException.ThrowIfNull(apply);

            lock (_verbs)
            {
                if (_sealed)
                {
                    throw new InvalidOperationException(
                        "The gate already moved past this hook, so this verb changes nothing. A hook is abandoned at its gate's deadline.");
                }

                if (_terminal)
                {
                    throw new InvalidOperationException("This gate already took a terminal verb, so no verb can follow it.");
                }

                apply();
                _terminal = terminal;
            }
        }
    }
}
