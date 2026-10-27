using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Where a <see cref="GamemodeSessionRunner"/> plays a gamemode, as the runtime decides by its realm.</summary>
    public enum GamemodeSessionRole
    {
        /// <summary>Single player: the server controller starts, then the player's participant controller.</summary>
        SinglePlayer = 0,

        /// <summary>The host of a multiplayer session: as single player, for the host's own player.</summary>
        Host = 1,

        /// <summary>A joined player: only the player's participant controller starts; the host runs the server controller.</summary>
        Client = 2
    }

    /// <summary>
    /// Starts and stops a gamemode factory by the runtime's rules, without the game. Where the role runs the session's
    /// server, in single player and on the host, <see cref="IGamemodeFactory.StartAsync"/> starts the server controller
    /// first; then a factory that implements <see cref="IGamemodeParticipantFactory"/> starts the player's participant
    /// controller, which is all a joined player's game starts. A failure or cancellation of either start ends the session
    /// and disposes what started; a participant's failure says it was the participant's, and every failure carries the code
    /// and the words a launch in the game reports. Stopping cancels the session's token, then disposes the participant
    /// controller, the server controller, the factory when it is disposable, and both contexts, attempting each whatever
    /// the one before it did.
    /// </summary>
    /// <remarks>
    /// The server and the participant run in contexts of their own, as their scopes do in the game, so register the fakes
    /// each controller resolves in the context it runs in.
    /// </remarks>
    public sealed class GamemodeSessionRunner : IDisposable
    {
        private const string ParticipantStartFailure = "The gamemode's participant controller failed to start: ";
        private const string StartupCancelled = "Session startup was cancelled.";
        private const string NoReason = "The session operation failed.";
        private const int MaximumReasonScalars = 4096;

        private readonly CancellationTokenSource session = new CancellationTokenSource();
        private readonly List<Exception> cleanupFailures = new List<Exception>();
        private bool started;
        private bool finished;

        /// <summary>Creates a runner for one session of <paramref name="factory"/> in <paramref name="role"/>.</summary>
        /// <param name="factory">The gamemode under test, constructed as the runtime would construct its declared type.</param>
        /// <param name="role">Where the session runs; single player by default.</param>
        /// <param name="serverContext">The server controller's context; a new one when null.</param>
        /// <param name="participantContext">The participant controller's own context; a new one for the same mod when null.</param>
        /// <param name="world">The world readiness both controllers see; a ready test world when null.</param>
        public GamemodeSessionRunner(IGamemodeFactory factory, GamemodeSessionRole role = GamemodeSessionRole.SinglePlayer,
            FakeModContext? serverContext = null, FakeModContext? participantContext = null, WorldReadiness? world = null)
        {
            Factory = factory ?? throw new ArgumentNullException(nameof(factory));
            if (role < GamemodeSessionRole.SinglePlayer || role > GamemodeSessionRole.Client) throw new ArgumentOutOfRangeException(nameof(role));
            Role = role;
            ServerContext = serverContext ?? new FakeModContext();
            ParticipantContext = participantContext ?? new FakeModContext(ServerContext.Identity);
            if (ReferenceEquals(ServerContext, ParticipantContext))
            {
                throw new ArgumentException(
                    "The participant controller runs in a scope of its own; give it a context of its own.",
                    nameof(participantContext));
            }

            World = world ?? new WorldReadiness(new WorldSceneIdentity(1, "test.world"), TransformState.Identity);
        }

        /// <summary>Gets the gamemode under test.</summary>
        public IGamemodeFactory Factory { get; }

        /// <summary>Gets where the session runs.</summary>
        public GamemodeSessionRole Role { get; }

        /// <summary>Gets the server controller's context, the stand-in for the gamemode server's scope.</summary>
        public FakeModContext ServerContext { get; }

        /// <summary>Gets the participant controller's context, the stand-in for the player's own scope.</summary>
        public FakeModContext ParticipantContext { get; }

        /// <summary>Gets the world readiness both controllers see.</summary>
        public WorldReadiness World { get; private set; }

        /// <summary>Gets the shared single-player view fake registered automatically when the session starts.</summary>
        [RequiresTopiaForge("0.2.0")]
        public FakeWorldView View { get; private set; } = new FakeWorldView();

        /// <summary>Supplies a configured view before startup; the runner owns its teardown.</summary>
        [RequiresTopiaForge("0.2.0")]
        public GamemodeSessionRunner WithView(FakeWorldView view)
        {
            if (started || finished) throw new InvalidOperationException("Supply the view before starting.");
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (!ReferenceEquals(View, view)) View.Dispose();
            View = view; return this;
        }

        /// <summary>Supplies a map before startup; stopping the runner disposes it when it owns a disposable fake.</summary>
        [RequiresTopiaForge("0.2.0")]
        public GamemodeSessionRunner WithMap(IAuthoredMap map)
        {
            if (started || finished || World.Map != null) throw new InvalidOperationException("Supply one map before starting the runner.");
            World = new WorldReadiness(World.Scene, World.Spawn, map ?? throw new ArgumentNullException(nameof(map)));
            return this;
        }

        /// <summary>Gets the server controller's session once it started, or null where no server runs.</summary>
        public FakeGamemodeSession? Session { get; private set; }

        /// <summary>Gets the participant controller's session once it started, or null for a gamemode without one.</summary>
        public FakeGamemodeParticipantSession? Participant { get; private set; }

        /// <summary>Gets the running server controller, or null.</summary>
        public IGamemodeController? Controller { get; private set; }

        /// <summary>Gets the running participant controller, or null.</summary>
        public IGamemodeParticipantController? ParticipantController { get; private set; }

        /// <summary>Gets whether the session started and has not stopped.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Gets the failures a failed start's cleanup and <see cref="Dispose"/> recorded, in order. <see cref="Stop"/>
        /// throws its own instead.
        /// </summary>
        public IReadOnlyList<Exception> CleanupFailures => cleanupFailures;

        /// <summary>
        /// Starts the session once. It succeeds only when every controller the role runs started; otherwise it returns the
        /// failure, after disposing what started. Cancelling <paramref name="cancellationToken"/> cancels the session's token.
        /// </summary>
        public async Task<OperationResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            if (started) throw new InvalidOperationException("This runner already started its session; use another runner for the next one.");
            started = true;
            OperationResult<bool>? failure;
            using (cancellationToken.Register(CancelSession))
            {
                failure = await StartControllersAsync().ConfigureAwait(false);
            }

            if (failure == null)
            {
                IsRunning = true;
                return OperationResult<bool>.Success(true);
            }

            CleanUp(cleanupFailures);
            return failure;
        }

        /// <summary>Stops the running session; throws what failed while stopping, after attempting every disposer.</summary>
        public void Stop()
        {
            if (!IsRunning) throw new InvalidOperationException("The gamemode session is not running.");
            var failures = new List<Exception>();
            CleanUp(failures);
            if (failures.Count == 1) throw failures[0];
            if (failures.Count > 1) throw new AggregateException("The gamemode session failed to stop cleanly.", failures);
        }

        /// <inheritdoc/>
        /// <remarks>Never throws, so an assertion failing inside a <c>using</c> block survives; see <see cref="CleanupFailures"/>.</remarks>
        public void Dispose()
        {
            if (!finished) CleanUp(cleanupFailures);
        }

        private async Task<OperationResult<bool>?> StartControllersAsync()
        {
            if (!ServerContext.Extensions.TryGet<IWorldView>(out _))
                ServerContext.Extensions.Register<IWorldView>(View.ForScope(ServerContext.Lifetime, role: Role));
            if (!ParticipantContext.Extensions.TryGet<IWorldView>(out _))
                ParticipantContext.Extensions.Register<IWorldView>(View.ForScope(ParticipantContext.Lifetime, participant: true, role: Role));
            var token = session.Token;
            if (Role != GamemodeSessionRole.Client)
            {
                Session = new FakeGamemodeSession(ServerContext, World, cancellationToken: token);
                OperationResult<IGamemodeController> server;
                try
                {
                    server = await Factory.StartAsync(Session, token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    return Thrown(exception, prefix: string.Empty);
                }

                if (server.TryGetValue(out var controller)) Controller = controller;
                if (token.IsCancellationRequested) return OperationResult<bool>.Failure(ModErrorCode.Cancelled, StartupCancelled);
                if (!server.Succeeded) return Failed(server.ErrorCode, Reason(server.ErrorMessage));
            }

            if (!(Factory is IGamemodeParticipantFactory participants)) return null;
            Participant = new FakeGamemodeParticipantSession(ParticipantContext, World, cancellationToken: token);
            OperationResult<IGamemodeParticipantController> participant;
            try
            {
                participant = await participants.StartParticipantAsync(Participant, token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return Thrown(exception, ParticipantStartFailure);
            }

            if (participant.TryGetValue(out var participantController)) ParticipantController = participantController;
            if (token.IsCancellationRequested) return OperationResult<bool>.Failure(ModErrorCode.Cancelled, StartupCancelled);
            if (participant.Succeeded) return null;
            var failure = Failed(participant.ErrorCode, participant.ErrorMessage);
            return failure.ErrorCode == ModErrorCode.Cancelled
                ? failure
                : OperationResult<bool>.Failure(failure.ErrorCode, Reason(ParticipantStartFailure + failure.ErrorMessage));
        }

        private OperationResult<bool> Thrown(Exception exception, string prefix)
        {
            if (session.IsCancellationRequested) return OperationResult<bool>.Failure(ModErrorCode.Cancelled, StartupCancelled);
            var reason = Reason(exception.GetBaseException().Message);
            return exception is OperationCanceledException
                ? OperationResult<bool>.Failure(ModErrorCode.Cancelled, reason)
                : OperationResult<bool>.Failure(ModErrorCode.External, Reason(prefix + reason));
        }

        /// <summary>
        /// A failed start as the game reports it: a code the SDK does not define fails as <see cref="ModErrorCode.External"/>,
        /// saying so, and the reason is worded as the game words it (see <see cref="Reason"/>).
        /// </summary>
        private static OperationResult<bool> Failed(ModErrorCode code, string? reason) =>
            code == ModErrorCode.None || !Enum.IsDefined(typeof(ModErrorCode), code)
                ? OperationResult<bool>.Failure(ModErrorCode.External,
                    Reason("An implementation returned an unsupported operation error code (" + (int)code + "). " + reason))
                : OperationResult<bool>.Failure(code, Reason(reason));

        /// <summary>
        /// A failure's reason as the game words it: "The session operation failed." when there is none, and at most 4,096
        /// Unicode characters, the last of them an ellipsis when the reason had to be cut.
        /// </summary>
        private static string Reason(string? text)
        {
            if (text == null || string.IsNullOrWhiteSpace(text)) return NoReason;
            var scalars = 0;
            var cut = 0;
            for (var offset = 0; offset < text.Length; offset++)
            {
                if (scalars == MaximumReasonScalars - 1) cut = offset;
                if (++scalars > MaximumReasonScalars) return text.Substring(0, cut) + "\u2026";
                if (char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1])) offset++;
            }

            return text;
        }

        private void CancelSession()
        {
            try
            {
                session.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void CleanUp(List<Exception> failures)
        {
            finished = true;
            IsRunning = false;
            Attempt(session.Cancel, failures);
            var participant = ParticipantController;
            var controller = Controller;
            ParticipantController = null;
            Controller = null;
            var disposed = new List<IDisposable>();
            foreach (var resource in new IDisposable?[] { participant, controller, Factory as IDisposable, View, World.Map as IDisposable, ParticipantContext, ServerContext })
            {
                if (resource == null || disposed.Exists(previous => ReferenceEquals(previous, resource))) continue;
                disposed.Add(resource);
                Attempt(resource.Dispose, failures);
            }

            session.Dispose();
        }

        private static void Attempt(Action action, List<Exception> failures)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }
}
