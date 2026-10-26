using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A live join's records: progress and outcomes V2 under its join id, as a launch request's are under its request
    /// (docs/internal/MultiplayerLaunchContract.md, section 6). They go through the launcher's channels on the background
    /// queue, since a refused write waits for the launcher's read, in order, under one sequence. A progress write that fails
    /// is left to the next one; an outcome is tried again, a few times. Session follow runs the host's world under the join's
    /// id (WS3.8), and its progress moves the records' phase under the join's block and names the host's world session, as the
    /// runtime's moves a launch request's, except while the join's own phase stands. So a success stands at Running with the
    /// host's world session once the follow runs it, and at Idle otherwise, as at a host still at its menu. Main thread only;
    /// nothing here names an address.
    /// </summary>
    internal sealed class LiveJoinRecords : ISessionLaunchSink
    {
        internal const int OutcomeAttempts = 3;

        private readonly ILauncherChannels channels;
        private readonly MultiplayerBackgroundQueue queue;
        private readonly LaunchSessionIdentity identity;
        private readonly Func<SessionPhase> localPhase;
        private readonly Func<string, Action<LaunchProgress>, IDisposable>? subscribe;
        private IDisposable? subscription;
        private LaunchProgressSession? block;
        private string? joinPhase;
        private LaunchProgress? followed;
        private int sequence = -1;
        private LaunchOutcomeMultiplayer? succeededBlock;
        private bool ended;
        private bool finished;

        /// <param name="localPhase">The local session's phase, which a record stands at while the join does not connect or synchronize.</param>
        /// <param name="sessionProgress">
        /// Subscribes to the session orchestrator's progress under a request, until the handle is disposed: the join's
        /// session follow reports under the join's id (<see cref="IMultiplayerSessionEnvironment.SubscribeSessionProgress"/>).
        /// Null where there is none.
        /// </param>
        internal LiveJoinRecords(ILauncherChannels channels, MultiplayerBackgroundQueue queue, LaunchSessionIdentity identity, Func<SessionPhase> localPhase,
            Func<string, Action<LaunchProgress>, IDisposable>? sessionProgress = null)
        {
            this.channels = channels ?? throw new ArgumentNullException(nameof(channels));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.localPhase = localPhase ?? throw new ArgumentNullException(nameof(localPhase));
            subscribe = sessionProgress;
            if (identity.Role != "join") throw new ArgumentException("A live join joins.", nameof(identity));
        }

        public bool HasResult { get; private set; }

        /// <summary>Gets whether the records take the join's session follow's progress now: from the join's first record until they end.</summary>
        internal bool TakesSessionProgress => subscription != null;

        public void Progress(LaunchProgressSession block, string? phase)
        {
            this.block = block ?? throw new ArgumentNullException(nameof(block));
            joinPhase = phase;
            if (subscription == null && !finished && subscribe != null) subscription = subscribe(identity.RequestId, SessionProgress);
            WriteProgress();
        }

        /// <summary>
        /// The progress of the session follow that runs the host's world for this join, under the join's id: a record under
        /// the join's block with the follow's phase and its world session, unless the join's own phase stands, as while it
        /// connects or synchronizes; then it waits for the join's next record. An older or another request's record moves nothing.
        /// </summary>
        internal void SessionProgress(LaunchProgress progress)
        {
            if (progress == null || finished || !string.Equals(progress.RequestId, identity.RequestId, StringComparison.Ordinal)) return;
            if (followed != null && progress.Sequence <= followed.Sequence) return;
            followed = progress;
            if (block != null && joinPhase == null) WriteProgress();
        }

        public void Result(SessionLaunchResult result, string? phase)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (HasResult) return;
            HasResult = true;
            var succeeded = result.Status == "succeeded";
            if (succeeded) succeededBlock = result.Multiplayer;
            else Finish();

            // In session in the host's world, its world session runs here; in session with a host at its menu, nothing runs yet.
            var running = succeeded && followed != null && followed.Phase == "running" && followed.SessionId != null;
            Write(new LaunchOutcome("launch", identity.RequestId, ++sequence, succeeded ? (running ? "running" : "idle") : phase ?? PhaseName(localPhase()),
                result.Status, Array.Empty<LaunchBlock>(), running ? followed!.SessionId : null, identity.Command, result.Error, result.Multiplayer), 1);
        }

        public void Ended(LaunchOutcomeMultiplayer block, bool clean)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            if (succeededBlock == null || ended) return;
            ended = true;
            var sessionId = followed?.SessionId;
            Finish();
            Write(new LaunchOutcome("session", identity.RequestId, ++sequence, "idle", clean ? "succeeded" : "failed", Array.Empty<LaunchBlock>(),
                sessionId, null, clean ? null : new LaunchExecutionError("external", "The runtime did not stop cleanly."), block), 1);
        }

        /// <summary>Gets the name a record gives the local session's phase.</summary>
        internal static string PhaseName(SessionPhase phase)
        {
            switch (phase)
            {
                case SessionPhase.Preparing:
                    return "preparing";
                case SessionPhase.LoadingWorld:
                    return "loading-world";
                case SessionPhase.StartingMode:
                    return "starting-mode";
                case SessionPhase.Running:
                    return "running";
                case SessionPhase.Stopping:
                    return "stopping";
                default:
                    return "idle";
            }
        }

        /// <summary>A progress record: the join's own phase while it connects or synchronizes, otherwise its follow's, or the local session's before any.</summary>
        private void WriteProgress()
        {
            var standing = joinPhase == null ? followed : null;
            var record = new LaunchProgress(identity.RequestId, ++sequence, joinPhase ?? standing?.Phase ?? PhaseName(localPhase()), standing?.SessionId,
                standing?.NativeBusy, block!);
            queue.Enqueue(() => channels.WriteProgress(record));
        }

        /// <summary>The records end: the join's session follow reports into them no more.</summary>
        private void Finish()
        {
            finished = true;
            var current = subscription;
            subscription = null;
            current?.Dispose();
        }

        private void Write(LaunchOutcome outcome, int attempt) =>
            queue.Enqueue(() => channels.WriteOutcome(outcome), failure =>
            {
                if (failure != null && attempt < OutcomeAttempts) Write(outcome, attempt + 1);
            });
    }
}
