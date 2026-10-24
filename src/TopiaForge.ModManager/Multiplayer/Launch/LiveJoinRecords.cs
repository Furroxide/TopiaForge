using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A live join's records: progress and outcomes V2 under its join id, as a launch request's are under its request
    /// (docs/internal/MultiplayerLaunchContract.md, section 6). They go through the launcher's channels on the background
    /// queue, since a refused write waits for the launcher's read, in order, under one sequence. A progress write that fails
    /// is left to the next one; an outcome is tried again, a few times. A live join is in session at the menu it left a game
    /// for, so its success stands at Idle. Main thread only; nothing here names an address.
    /// </summary>
    internal sealed class LiveJoinRecords : ISessionLaunchSink
    {
        internal const int OutcomeAttempts = 3;

        private readonly ILauncherChannels channels;
        private readonly MultiplayerBackgroundQueue queue;
        private readonly LaunchSessionIdentity identity;
        private readonly Func<SessionPhase> localPhase;
        private int sequence = -1;
        private LaunchOutcomeMultiplayer? succeededBlock;
        private bool ended;

        /// <param name="localPhase">The local session's phase, which a record stands at while the join does not connect or synchronize.</param>
        internal LiveJoinRecords(ILauncherChannels channels, MultiplayerBackgroundQueue queue, LaunchSessionIdentity identity, Func<SessionPhase> localPhase)
        {
            this.channels = channels ?? throw new ArgumentNullException(nameof(channels));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.localPhase = localPhase ?? throw new ArgumentNullException(nameof(localPhase));
            if (identity.Role != "join") throw new ArgumentException("A live join joins.", nameof(identity));
        }

        public bool HasResult { get; private set; }

        public void Progress(LaunchProgressSession block, string? phase)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            var record = new LaunchProgress(identity.RequestId, ++sequence, phase ?? PhaseName(localPhase()), null, null, block);
            queue.Enqueue(() => channels.WriteProgress(record));
        }

        public void Result(SessionLaunchResult result, string? phase)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (HasResult) return;
            HasResult = true;
            var succeeded = result.Status == "succeeded";
            if (succeeded) succeededBlock = result.Multiplayer;
            Write(new LaunchOutcome("launch", identity.RequestId, ++sequence, succeeded ? "idle" : phase ?? PhaseName(localPhase()), result.Status,
                Array.Empty<LaunchBlock>(), null, identity.Command, result.Error, result.Multiplayer), 1);
        }

        public void Ended(LaunchOutcomeMultiplayer block, bool clean)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            if (succeededBlock == null || ended) return;
            ended = true;
            Write(new LaunchOutcome("session", identity.RequestId, ++sequence, "idle", clean ? "succeeded" : "failed", Array.Empty<LaunchBlock>(),
                null, null, clean ? null : new LaunchExecutionError("external", "The runtime did not stop cleanly."), block), 1);
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

        private void Write(LaunchOutcome outcome, int attempt) =>
            queue.Enqueue(() => channels.WriteOutcome(outcome), failure =>
            {
                if (failure != null && attempt < OutcomeAttempts) Write(outcome, attempt + 1);
            });
    }
}
