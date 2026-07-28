using System;

namespace TopiaForge.Mods
{
    /// <summary>A released-on-dispose hold over some part of the running game.</summary>
    /// <remarks>
    /// Implemented by every SDK lease that a mod can hold and let go, so helpers such as
    /// <see cref="GameplayPause"/> can own one without caring which service issued it.
    /// </remarks>
    public interface IGameplayLease : IDisposable
    {
        /// <summary>Gets whether this lease has not yet been released.</summary>
        bool IsActive { get; }
    }

    /// <summary>Identifies what is currently holding a <see cref="GameplayPause"/>.</summary>
    public enum GameplayPauseKind
    {
        /// <summary>Nothing is held.</summary>
        None = 0,

        /// <summary>The preferred source is held — normally a Chronos world freeze.</summary>
        Preferred = 1,

        /// <summary>The preferred source was unavailable, so player control alone is suspended.</summary>
        PlayerControl = 2
    }

    /// <summary>
    /// Holds gameplay still for modal UI — a shop, an inventory, a dialogue window, a game-over screen — and keeps
    /// trying to reacquire that hold if the game takes it away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pausing correctly is not one call. The strong option is a Chronos world freeze, but Chronos is an optional
    /// module and its engine hooks can be unresolved, so the honest fallback is suspending player control alone.
    /// Either hold can also be lost mid-session — a scene change, another mod's lease, a provider reload — and a
    /// modal window that silently stops pausing is worse than one that never paused. So the acquisition has to
    /// retry, and the failure has to be reported once rather than every frame.
    /// </para>
    /// <para>
    /// That is this type. Call <see cref="Request"/> when the window opens, <see cref="Tick"/> once per frame from
    /// the mod's update with an <em>unscaled</em> delta (a scaled clock stops while the world is frozen, which
    /// would freeze the retry loop too), and <see cref="Release"/> when the window closes. Disposal is idempotent
    /// and safe after partial construction.
    /// </para>
    /// <example>
    /// <code>
    /// // Chronos is optional; AsPauseSource() degrades to player control on its own.
    /// pause = new GameplayPause(Context, "mymod-shop", time.AsPauseSource(), "MYMOD_SHOP_PAUSE_FAILED");
    ///
    /// void OpenShop()  => pause.Request();
    /// void CloseShop() => pause.Release();
    /// void OnUpdate(float _) => pause.Tick(Context.Time.Frame.UnscaledDeltaTime);
    /// </code>
    /// </example>
    /// </remarks>
    public sealed class GameplayPause : IDisposable
    {
        /// <summary>How long to wait before retrying a failed acquisition, in unscaled seconds.</summary>
        public const float DefaultRetrySeconds = 0.5f;

        private readonly IModContext context;
        private readonly string usage;
        private readonly Func<string, OperationResult<IGameplayLease>>? preferred;
        private readonly string diagnosticCode;
        private readonly float retrySeconds;

        private IGameplayLease? preferredLease;
        private IPlayerControlLease? controlLease;
        private float retryTimer;
        private bool failureReported;
        private bool requested;
        private bool disposed;

        /// <summary>Creates a pause that has not yet been requested.</summary>
        /// <param name="context">The owning mod context.</param>
        /// <param name="usage">
        /// A stable diagnostic id for this pause, such as <c>"mymod-shop"</c>. Composing providers use it to
        /// attribute the hold, so keep it unique per surface.
        /// </param>
        /// <param name="preferred">
        /// The preferred hold, normally <c>timeControlService.AsPauseSource()</c>. Pass <c>null</c> to suspend
        /// player control only.
        /// </param>
        /// <param name="diagnosticCode">
        /// A stable code reported through <see cref="IDiagnosticsService"/> the first time acquisition fails
        /// completely. Pass an empty string to report nothing.
        /// </param>
        /// <param name="retrySeconds">How long to wait between retries. Defaults to <see cref="DefaultRetrySeconds"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="usage"/> is null or blank.</exception>
        public GameplayPause(
            IModContext context,
            string usage,
            Func<string, OperationResult<IGameplayLease>>? preferred = null,
            string diagnosticCode = "",
            float retrySeconds = DefaultRetrySeconds)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(usage))
            {
                throw new ArgumentException("A stable pause usage id is required.", nameof(usage));
            }

            this.usage = usage;
            this.preferred = preferred;
            this.diagnosticCode = diagnosticCode ?? string.Empty;
            this.retrySeconds = float.IsNaN(retrySeconds) || retrySeconds < 0f
                ? DefaultRetrySeconds
                : retrySeconds;
        }

        /// <summary>Gets whether gameplay is currently held.</summary>
        public bool IsActive => preferredLease?.IsActive == true || controlLease?.IsActive == true;

        /// <summary>Gets whether the pause is wanted but not currently held, so <see cref="Tick"/> is retrying.</summary>
        public bool IsRetrying => requested && !IsActive;

        /// <summary>Gets what is holding gameplay right now.</summary>
        public GameplayPauseKind Kind => preferredLease?.IsActive == true
            ? GameplayPauseKind.Preferred
            : controlLease?.IsActive == true
                ? GameplayPauseKind.PlayerControl
                : GameplayPauseKind.None;

        /// <summary>Requests the pause and attempts to acquire it immediately.</summary>
        /// <remarks>Safe to call when already active; the existing hold is kept.</remarks>
        public void Request()
        {
            if (disposed)
            {
                return;
            }

            requested = true;
            if (IsActive)
            {
                return;
            }

            Acquire();
        }

        /// <summary>
        /// Reacquires the pause if it was lost. Call once per frame with an unscaled delta while the pause is wanted.
        /// </summary>
        /// <param name="unscaledDeltaTime">Elapsed unscaled seconds since the last call.</param>
        public void Tick(float unscaledDeltaTime)
        {
            if (disposed || !requested)
            {
                return;
            }

            if (IsActive)
            {
                retryTimer = 0f;
                failureReported = false;
                return;
            }

            var delta = float.IsNaN(unscaledDeltaTime) || unscaledDeltaTime < 0f ? 0f : unscaledDeltaTime;
            retryTimer = Math.Max(0f, retryTimer - delta);
            if (retryTimer > 0f)
            {
                return;
            }

            ReleaseLeases();
            Acquire();
        }

        /// <summary>Releases the pause and stops retrying. Safe to call when nothing is held.</summary>
        public void Release()
        {
            requested = false;
            retryTimer = 0f;
            failureReported = false;
            ReleaseLeases();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Release();
        }

        private void Acquire()
        {
            var preferredError = string.Empty;
            if (preferred != null)
            {
                var result = preferred(usage);
                if (result.TryGetValue(out var lease) && lease.IsActive)
                {
                    preferredLease = lease;
                    retryTimer = 0f;
                    failureReported = false;
                    return;
                }

                preferredError = result.ErrorMessage;
            }

            var fallback = context.LocalPlayer.AcquireControl(usage);
            if (fallback.TryGetValue(out var control))
            {
                controlLease = control;
                retryTimer = 0f;
                failureReported = false;
                return;
            }

            retryTimer = retrySeconds;
            if (failureReported || diagnosticCode.Length == 0)
            {
                return;
            }

            failureReported = true;
            context.Diagnostics.Report(new DiagnosticEntry(
                diagnosticCode,
                "'" + usage + "' could not pause gameplay; acquisition will retry in the background.",
                DiagnosticSeverity.Warning,
                preferredError.Length == 0
                    ? fallback.ErrorMessage
                    : "Preferred: " + preferredError + " Player fallback: " + fallback.ErrorMessage));
        }

        private void ReleaseLeases()
        {
            preferredLease?.Dispose();
            preferredLease = null;
            controlLease?.Dispose();
            controlLease = null;
        }
    }
}
