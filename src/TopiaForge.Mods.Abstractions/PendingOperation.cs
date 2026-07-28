using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>The state reported by one <see cref="PendingOperation{T}.Poll"/> call.</summary>
    public enum PendingOperationState
    {
        /// <summary>Nothing is armed or draining.</summary>
        Idle = 0,

        /// <summary>Work is still running; poll again next frame.</summary>
        Waiting = 1,

        /// <summary>The armed operation finished and its result is wanted by the caller.</summary>
        Completed = 2,

        /// <summary>
        /// The armed operation exceeded its budget. It is reported once so the caller can recover, and keeps
        /// draining so a late result is still released.
        /// </summary>
        TimedOut = 3,

        /// <summary>
        /// A cancelled or timed-out operation produced a result after the caller stopped wanting it. The caller
        /// must release anything the result owns.
        /// </summary>
        Abandoned = 4,
    }

    /// <summary>
    /// Runs one asynchronous SDK operation at a time and hands its result back on the caller's own thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asynchronous SDK work — asset bundles, prefabs, custom-world content, scene loads — is driven by the
    /// game's own loader and completes on the main thread. Blocking that thread to wait for it stops the frame
    /// loop that would have completed it, so the operation never finishes and the game hangs with no recovery.
    /// The analyzer reports a blocking wait as TF1008.
    /// </para>
    /// <para>
    /// This is the supported alternative: start the operation, then call <see cref="Poll"/> from a per-frame
    /// update and act on the state it returns. Nothing here ever waits.
    /// </para>
    /// <para>
    /// A cancelled or timed-out operation is not dropped. The SDK may still hand back a live result that owns
    /// game objects, and only the main thread may release those, so it keeps draining and is reported as
    /// <see cref="PendingOperationState.Abandoned"/>. That also frees the armed slot immediately, so restarting
    /// never has to wait on work the caller already discarded.
    /// </para>
    /// <example>
    /// <code>
    /// private readonly PendingOperation&lt;IAssetBundle&gt; load = new PendingOperation&lt;IAssetBundle&gt;();
    ///
    /// private void StartLoad()
    /// {
    ///     load.Begin(
    ///         token =&gt; Context.Assets.LoadBundleAsync("content/level.bundle", token),
    ///         Context.Lifetime.StoppingToken,
    ///         Context.Time.Frame.UnscaledTime);
    /// }
    ///
    /// private void OnUpdate(float deltaTime)
    /// {
    ///     switch (load.Poll(Context.Time.Frame.UnscaledTime, timeoutSeconds: 30f, out var result))
    ///     {
    ///         case PendingOperationState.Completed when result.TryGetValue(out var bundle):
    ///             Use(bundle);
    ///             break;
    ///         case PendingOperationState.Abandoned when result.TryGetValue(out var orphan):
    ///             orphan.Dispose();
    ///             break;
    ///     }
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    /// <typeparam name="T">The operation's success value.</typeparam>
    public sealed class PendingOperation<T> where T : notnull
    {
        private readonly List<Entry> draining = new List<Entry>();
        private Entry? active;
        private float startedAt;

        /// <summary>Gets whether an operation is armed and still wanted by the caller.</summary>
        public bool IsInFlight => active != null;

        /// <summary>
        /// Starts one operation.
        /// </summary>
        /// <param name="start">
        /// Starts the work. It receives a token cancelled by <see cref="Cancel"/>, by the timeout in
        /// <see cref="Poll"/>, or by <paramref name="linkedTo"/>. Pass it straight to the SDK call.
        /// </param>
        /// <param name="linkedTo">
        /// An outer token, normally <c>IModLifetime.StoppingToken</c>, so unloading cancels the work.
        /// </param>
        /// <param name="now">
        /// The current time on any monotonic clock, used only to measure the <see cref="Poll"/> timeout. Use the
        /// same clock for both calls; unscaled time is the usual choice so a frozen game still times out.
        /// </param>
        /// <exception cref="InvalidOperationException">An operation is already armed.</exception>
        public void Begin(
            Func<CancellationToken, Task<OperationResult<T>>> start,
            CancellationToken linkedTo,
            float now)
        {
            if (start == null)
            {
                throw new ArgumentNullException(nameof(start));
            }

            if (active != null)
            {
                throw new InvalidOperationException(
                    "The armed operation must be released or cancelled before another can begin.");
            }

            var source = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
            try
            {
                var started = start(source.Token)
                    ?? throw new InvalidOperationException("The operation factory returned no task.");
                active = new Entry(started, source);
                startedAt = now;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Drains work on the caller's thread without ever waiting. Call this from a per-frame update.
        /// </summary>
        /// <param name="now">The current time on the same clock passed to <see cref="Begin"/>.</param>
        /// <param name="timeoutSeconds">
        /// How long the armed operation may run before <see cref="PendingOperationState.TimedOut"/> is reported
        /// once. Pass <see cref="float.PositiveInfinity"/> to rely solely on cancellation.
        /// </param>
        /// <param name="result">
        /// The operation's result. Meaningful only for <see cref="PendingOperationState.Completed"/> and
        /// <see cref="PendingOperationState.Abandoned"/>.
        /// </param>
        /// <returns>What the caller should do this frame.</returns>
        public PendingOperationState Poll(
            float now,
            float timeoutSeconds,
            out OperationResult<T> result)
        {
            // Abandoned results are reported ahead of the armed operation so discarded work is released promptly.
            for (var index = 0; index < draining.Count; index++)
            {
                var entry = draining[index];
                if (!entry.Task.IsCompleted)
                {
                    continue;
                }

                draining.RemoveAt(index);
                result = entry.Complete();
                return PendingOperationState.Abandoned;
            }

            var current = active;
            if (current == null)
            {
                result = NoResult();
                return draining.Count > 0 ? PendingOperationState.Waiting : PendingOperationState.Idle;
            }

            if (current.Task.IsCompleted)
            {
                active = null;
                startedAt = 0f;
                result = current.Complete();
                return PendingOperationState.Completed;
            }

            if (now - startedAt < timeoutSeconds)
            {
                result = NoResult();
                return PendingOperationState.Waiting;
            }

            Abandon(current);
            result = NoResult();
            return PendingOperationState.TimedOut;
        }

        /// <summary>
        /// Abandons the armed operation. It keeps draining until it produces a result, so any late value is
        /// released on the caller's thread, and the armed slot is free to restart immediately.
        /// </summary>
        public void Cancel()
        {
            var current = active;
            if (current != null)
            {
                Abandon(current);
            }
        }

        /// <summary>
        /// Drops all work without draining it. Use this only once the owning mod lifetime is stopping: the
        /// runtime then owns whatever the SDK produces and releases it with the rest of the lifetime.
        /// </summary>
        public void Forget()
        {
            active?.Discard();
            active = null;
            startedAt = 0f;
            for (var index = 0; index < draining.Count; index++)
            {
                draining[index].Discard();
            }

            draining.Clear();
        }

        private void Abandon(Entry entry)
        {
            entry.Cancel();
            active = null;
            startedAt = 0f;
            draining.Add(entry);
        }

        private static OperationResult<T> NoResult() =>
            OperationResult<T>.Failure(ModErrorCode.InvalidState, "No operation result is available.");

        private sealed class Entry
        {
            private readonly CancellationTokenSource cancellation;

            public Entry(Task<OperationResult<T>> task, CancellationTokenSource cancellation)
            {
                Task = task;
                this.cancellation = cancellation;
            }

            public Task<OperationResult<T>> Task { get; }

            public void Cancel()
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already released; the operation is no longer observable either way.
                }
            }

            /// <summary>Reads the completed result. Callers confirm <c>Task.IsCompleted</c> first.</summary>
            public OperationResult<T> Complete()
            {
                try
                {
                    return Task.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return OperationResult<T>.Failure(ModErrorCode.Cancelled, "The operation was cancelled.");
                }
                catch (Exception exception)
                {
                    return OperationResult<T>.Failure(ModErrorCode.External, exception.Message);
                }
                finally
                {
                    cancellation.Dispose();
                }
            }

            public void Discard()
            {
                Cancel();
                cancellation.Dispose();
            }
        }
    }
}
