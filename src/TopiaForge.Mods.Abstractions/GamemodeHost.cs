using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Owns the wiring between a Worlds gamemode and the object that runs one session of it, so a gamemode entry
    /// point is left with only the parts that are actually about the gamemode.
    /// </summary>
    /// <typeparam name="TController">
    /// The per-session coordinator. One instance exists per active session and is disposed when the session ends,
    /// the active gamemode changes, or the mod unloads.
    /// </typeparam>
    /// <remarks>
    /// <para>
    /// Hosting a gamemode by hand is about eighty lines that every author has to get right in the same order:
    /// register the gamemode and the menu entry, roll the first registration back if the second fails, subscribe to
    /// session changes, defer the unsubscribe onto the mod lifetime, <em>replay the session that is already
    /// running</em> (missing this is why a hot reload mid-session produces a mod that never wakes up), match the
    /// gamemode id, keep exactly one controller alive, dispose the previous one before building the next, and tear
    /// everything down idempotently. This type is that sequence, written once.
    /// </para>
    /// <para>
    /// Registration is optional. Pass <c>gamemode</c> and <c>menuEntry</c> to publish a new
    /// gamemode, or omit them to attach to one the provider already offers.
    /// </para>
    /// <example>
    /// <code>
    /// var host = GamemodeHost&lt;MyController&gt;.Create(
    ///     Context,
    ///     Context.RequireExtension&lt;IWorldGamemodeService&gt;(),
    ///     GamemodeId,
    ///     session =&gt; new MyController(Context, session),
    ///     new GamemodeDefinition(GamemodeId, "My Mode", "..."),
    ///     new GamemodeMenuEntry(MenuId, "My Mode", "...", GamemodeId));
    /// if (host.TryGetValue(out var hosted))
    /// {
    ///     hosted.AddPauseAction(new WorldPauseAction("restart", "RESTART RUN", () =&gt; hosted.Controller?.Restart()));
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    public sealed class GamemodeHost<TController> : IDisposable
        where TController : class, IDisposable
    {
        private readonly IModContext context;
        private readonly IWorldGamemodeService worlds;
        private readonly string gamemodeId;
        private readonly Func<WorldSession, TController> createController;
        private readonly List<WorldPauseAction> pauseActions = new List<WorldPauseAction>();
        private readonly List<IDisposable> sessionPauseHandles = new List<IDisposable>();

        private IWorldRegistration? gamemodeRegistration;
        private IWorldRegistration? menuRegistration;
        private IDisposable? controllerLease;
        private bool disposed;

        private GamemodeHost(
            IModContext context,
            IWorldGamemodeService worlds,
            string gamemodeId,
            Func<WorldSession, TController> createController)
        {
            this.context = context;
            this.worlds = worlds;
            this.gamemodeId = gamemodeId;
            this.createController = createController;
        }

        /// <summary>Gets the controller for the active session, or null when no session of this gamemode is running.</summary>
        public TController? Controller { get; private set; }

        /// <summary>Gets whether a session of this gamemode is currently hosted.</summary>
        public bool IsSessionActive => Controller != null;

        /// <summary>Registers the gamemode (when supplied) and begins hosting its sessions.</summary>
        /// <param name="context">The owning mod context.</param>
        /// <param name="worlds">The Worlds service, normally from <c>Context.RequireExtension</c>.</param>
        /// <param name="gamemodeId">The gamemode id whose sessions this host owns.</param>
        /// <param name="createController">
        /// Builds the coordinator for one session. Throwing from here is treated as a failed session: the partially
        /// built controller is disposed, a diagnostic is reported, and the session is ended with
        /// <see cref="WorldSessionEndReason.LoadFailed"/> rather than leaving the player in a broken world.
        /// </param>
        /// <param name="gamemode">A gamemode to publish, or null to attach to an existing one.</param>
        /// <param name="menuEntry">A menu entry to publish, or null for none.</param>
        /// <returns>
        /// The host, or a typed failure when a supplied registration was rejected. A partial registration is rolled
        /// back before the failure is returned, so a rejected menu entry never leaves an orphaned gamemode.
        /// </returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="gamemodeId"/> is null or blank.</exception>
        public static OperationResult<GamemodeHost<TController>> Create(
            IModContext context,
            IWorldGamemodeService worlds,
            string gamemodeId,
            Func<WorldSession, TController> createController,
            GamemodeDefinition? gamemode = null,
            GamemodeMenuEntry? menuEntry = null)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (worlds == null)
            {
                throw new ArgumentNullException(nameof(worlds));
            }

            if (createController == null)
            {
                throw new ArgumentNullException(nameof(createController));
            }

            if (string.IsNullOrWhiteSpace(gamemodeId))
            {
                throw new ArgumentException("A gamemode id is required.", nameof(gamemodeId));
            }

            var host = new GamemodeHost<TController>(context, worlds, gamemodeId, createController);
            if (gamemode != null)
            {
                var registered = worlds.RegisterGamemode(gamemode);
                if (!registered.TryGetValue(out var gamemodeHandle))
                {
                    return OperationResult<GamemodeHost<TController>>.Failure(
                        registered.ErrorCode,
                        "The gamemode could not be registered: " + registered.ErrorMessage);
                }

                host.gamemodeRegistration = gamemodeHandle;
            }

            if (menuEntry != null)
            {
                var registered = worlds.RegisterMenuEntry(menuEntry);
                if (!registered.TryGetValue(out var menuHandle))
                {
                    host.gamemodeRegistration?.Dispose();
                    host.gamemodeRegistration = null;
                    return OperationResult<GamemodeHost<TController>>.Failure(
                        registered.ErrorCode,
                        "The gamemode menu entry could not be registered: " + registered.ErrorMessage);
                }

                host.menuRegistration = menuHandle;
            }

            worlds.SessionChanged += host.OnSessionChanged;
            worlds.SessionEnded += host.OnSessionEnded;
            context.Lifetime.Defer(() =>
            {
                worlds.SessionChanged -= host.OnSessionChanged;
                worlds.SessionEnded -= host.OnSessionEnded;
            });
            context.Lifetime.Track(host);

            // A session may already be running — after a hot reload, or when this mod loads late in the order.
            // Without this replay the host would stay dormant until the player changed gamemode.
            var current = worlds.CurrentSession;
            if (current != null)
            {
                host.OnSessionChanged(current);
            }

            return OperationResult<GamemodeHost<TController>>.Success(host);
        }

        /// <summary>
        /// Adds a pause-menu action offered while a session of this gamemode is active.
        /// </summary>
        /// <remarks>
        /// The action is registered for the current session if one is running, and re-registered automatically for
        /// every later session, so callers do not repeat this per session. Actions are released when the session
        /// ends. The pause menu is best-effort: a host that cannot reach the game's pause UI reports a debug message
        /// rather than failing the gamemode.
        /// </remarks>
        /// <param name="action">The action to offer.</param>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
        public void AddPauseAction(WorldPauseAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (disposed)
            {
                return;
            }

            pauseActions.Add(action);
            if (Controller != null)
            {
                RegisterPauseAction(action);
            }
        }

        /// <summary>Ends the current session's controller and releases every registration.</summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            StopSession();
            menuRegistration?.Dispose();
            menuRegistration = null;
            gamemodeRegistration?.Dispose();
            gamemodeRegistration = null;
        }

        private void OnSessionChanged(WorldSession session)
        {
            if (disposed)
            {
                return;
            }

            if (session == null
                || !string.Equals(session.GamemodeId, gamemodeId, StringComparison.OrdinalIgnoreCase))
            {
                StopSession();
                return;
            }

            StopSession();

            TController? created = null;
            try
            {
                created = createController(session);
                if (created == null)
                {
                    return;
                }

                controllerLease = context.Lifetime.Track(created);
                Controller = created;
            }
            catch (Exception exception)
            {
                try
                {
                    created?.Dispose();
                }
                catch (Exception cleanupException)
                {
                    context.Logger.Warn(
                        "Failed-session cleanup for '" + gamemodeId + "' also failed: " + cleanupException.Message);
                }

                Controller = null;
                controllerLease = null;
                context.Diagnostics.Report(new DiagnosticEntry(
                    "GAMEMODE_SESSION_START_FAILED",
                    "The gamemode '" + gamemodeId + "' could not start a playable session.",
                    DiagnosticSeverity.Error,
                    exception.Message));
                worlds.EndSession(WorldSessionEndReason.LoadFailed);
                return;
            }

            for (var index = 0; index < pauseActions.Count; index++)
            {
                RegisterPauseAction(pauseActions[index]);
            }
        }

        private void OnSessionEnded(WorldSessionEnd ended) => StopSession();

        private void RegisterPauseAction(WorldPauseAction action)
        {
            if (!context.TryGetExtension<IWorldPauseMenuService>(out var pauseMenu))
            {
                return;
            }

            var result = pauseMenu.RegisterAction(action);
            if (result.TryGetValue(out var handle))
            {
                sessionPauseHandles.Add(handle);
                return;
            }

            context.Logger.Debug(
                "Pause action '" + action.Id + "' is unavailable: " + result.ErrorMessage);
        }

        private void StopSession()
        {
            for (var index = sessionPauseHandles.Count - 1; index >= 0; index--)
            {
                sessionPauseHandles[index].Dispose();
            }

            sessionPauseHandles.Clear();
            Controller = null;
            var lease = controllerLease;
            controllerLease = null;
            lease?.Dispose();
        }
    }
}
