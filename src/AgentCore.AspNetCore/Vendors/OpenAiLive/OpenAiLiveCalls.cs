using System.Collections.Concurrent;
using AgentCore.Application.Conversation.Actions;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive
{
    /// <summary>
    /// Owns every live GPT-Live call of the process, because a call outlives the webhook request. It also
    /// remembers webhook ids and claims call ids, so one process never admits one call twice, checks the
    /// adapter's keys at start, and hangs up and ends each live call at shutdown.
    /// </summary>
    internal sealed class OpenAiLiveCalls(IServiceProvider services) : IHostedService, IDisposable
    {
        /// <summary>How long a webhook id is remembered. OpenAI retries a webhook within minutes.</summary>
        internal static readonly TimeSpan WebhookMemory = TimeSpan.FromMinutes(10);

        private readonly ConcurrentDictionary<string, DateTimeOffset> _webhooks = new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, byte> _claimed = new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<OpenAiLiveCall, Task> _running = new();

        private readonly CancellationTokenSource _stopping = new();

        /// <summary>
        /// Gets the host's own transfer, or <see langword="null"/> when it registered none. It comes from the root
        /// services: a call outlives the request that started it, so a scoped one would be used after its scope ended.
        /// </summary>
        internal ICallTransfer? HostTransfer => services.GetService<ICallTransfer>();

        private readonly Lock _gate = new();

        private bool _stopped;

        /// <summary>Gets the token of the host's stop; a disposed registry reads as stopped.</summary>
        internal CancellationToken Stopping
        {
            get
            {
                try
                {
                    return _stopping.Token;
                }
                catch (ObjectDisposedException)
                {
                    return new CancellationToken(canceled: true);
                }
            }
        }

        internal bool TryClaimWebhook(string webhookId, DateTimeOffset now)
        {
            foreach (KeyValuePair<string, DateTimeOffset> seen in _webhooks)
            {
                if (now - seen.Value > WebhookMemory)
                {
                    _ = _webhooks.TryRemove(seen);
                }
            }

            return _webhooks.TryAdd(webhookId, now);
        }

        internal bool TryClaimCall(string callId) => _claimed.TryAdd(callId, 0);

        internal void ReleaseCall(string callId) => _claimed.TryRemove(callId, out _);

        /// <summary>Forgets a webhook id whose request failed, so the retry OpenAI sends under the same id is processed.</summary>
        internal void ReleaseWebhook(string webhookId) => _webhooks.TryRemove(webhookId, out _);

        /// <summary>
        /// Runs a call to its end. A call that arrives after the host began to stop is hung up and ended with cause
        /// <see cref="OpenAiLiveCall.ShutdownCause"/> instead: no call outlives the host.
        /// </summary>
        internal void Run(OpenAiLiveCall call)
        {
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool late;
            lock (_gate)
            {
                late = _stopped;
                _running[call] = done.Task;
            }

            _ = RunAsync(call, late, done);
        }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            AgentCoreBoot boot = services.GetRequiredService<AgentCoreBoot>();
            foreach (OpenAiLiveConversationAdapter adapter in boot.ConversationAdapters?.OfType<OpenAiLiveConversationAdapter>() ?? [])
            {
                if (adapter.Credentials is { } pending)
                {
                    _ = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            OpenAiLiveCall[] live;
            lock (_gate)
            {
                _stopped = true;
                live = [.. _running.Keys];
            }

            try
            {
                await Task.WhenAll(live.Select(call => call.EndAsync(ConversationEndReason.Faulted, OpenAiLiveCall.ShutdownCause, cancellationToken)))
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await _stopping.CancelAsync().ConfigureAwait(false);

                // A call that registered after the snapshot above ends itself as late; wait for it too.
                while (!_running.IsEmpty)
                {
                    await Task.WhenAll(_running.Values).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _stopping.Dispose();
        }

        private async Task RunAsync(OpenAiLiveCall call, bool late, TaskCompletionSource done)
        {
            await Task.Yield();
            try
            {
                if (late)
                {
                    try
                    {
                        await call.EndAsync(ConversationEndReason.Faulted, OpenAiLiveCall.ShutdownCause, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception fault) when (fault is not OutOfMemoryException)
                    {
                        OpenAiLiveLog.RunFaulted(call.Logger, call.CallId, fault);
                    }
                }

                // With the token already cancelled, RunAsync only waits for the answers and disposes the call and its sideband.
                await call.RunAsync(late ? new CancellationToken(canceled: true) : Stopping).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OutOfMemoryException)
            {
                OpenAiLiveLog.RunFaulted(call.Logger, call.CallId, fault);
            }
            finally
            {
                _ = _running.TryRemove(call, out _);
                ReleaseCall(call.CallId);
                _ = done.TrySetResult();
            }
        }
    }
}
