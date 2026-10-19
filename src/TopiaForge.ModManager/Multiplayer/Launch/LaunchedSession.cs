using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A planned session launch as the screens start it (<see cref="ILaunchedSession"/>), reporting into its sink: the
    /// runtime's publisher for the session this game was launched to host or join, or a live join's own records. It follows
    /// the provider's status into the launch's progress, its one result and, once the session started, its end. A frame
    /// whose status has not moved allocates nothing. Main thread only; nothing here holds an address.
    /// </summary>
    internal sealed class LaunchedSession : ILaunchedSession
    {
        private readonly ISessionLaunchSink sink;
        private readonly Func<LaunchedGame> game;
        private readonly int hostPort;
        private int followed = -1;
        private bool followedStarted;
        private LaunchProgressSession block;
        private string? reachedPhase;
        private bool synchronized;
        private bool succeeded;

        /// <param name="game">Where the launch's own game stands; a live join's is always ready.</param>
        /// <param name="hostPort">The port a launched host was to use, for the words of a port that was taken; zero for any.</param>
        internal LaunchedSession(LaunchSessionIdentity identity, IInternalLaunchPlan plan, ISessionLaunchSink sink, Func<LaunchedGame> game,
            bool liveJoin = false, bool startsGame = false, LauncherHandoff? handoff = null, int hostPort = 0)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.game = game ?? throw new ArgumentNullException(nameof(game));
            if ((identity.Role == "host") != (plan.Host != null)) throw new ArgumentException("The plan plays the launch's role.", nameof(plan));
            IsLiveJoin = liveJoin;
            StartsGame = startsGame;
            Handoff = handoff;
            this.hostPort = hostPort;
            block = new LaunchProgressSession(identity.Role, identity.IntentId, "pending");
        }

        internal LaunchSessionIdentity Identity { get; }

        public bool IsHost => Identity.Role == "host";

        public bool IsLiveJoin { get; }

        public bool StartsGame { get; }

        public LauncherHandoff? Handoff { get; }

        public IInternalLaunchPlan Plan { get; }

        public LaunchedGame Game => game();

        public bool HasStarted { get; private set; }

        public bool IsSettled { get; private set; }

        public bool IsFinished { get; private set; }

        public void Started()
        {
            if (!IsSettled) HasStarted = true;
        }

        public void Follow(InternalLiveStatus status, int version)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (IsFinished || (version == followed && HasStarted == followedStarted)) return;
            followed = version;
            followedStarted = HasStarted;

            // The runtime's own command failed first, as when the host's target did not start: that outcome is the launch's.
            if (!IsSettled && sink.HasResult)
            {
                IsSettled = true;
                IsFinished = true;
                return;
            }

            var next = SessionLaunchRecords.Progress(Identity, HasStarted, status, succeeded);
            if (!Same(next, block))
            {
                block = next;
                var phase = SessionLaunchRecords.PhaseOf(next);
                if (phase != null) reachedPhase = phase;
                if (next.State == "synchronizing" || next.State == "in-session") synchronized = true;
                sink.Progress(next, phase);
            }

            if (!HasStarted) return;
            if (!IsSettled)
            {
                var result = SessionLaunchRecords.Result(Identity, status, synchronized, hostPort);
                if (result == null) return;
                IsSettled = true;
                succeeded = result.Status == "succeeded";
                IsFinished = !succeeded;
                sink.Result(result, succeeded ? null : reachedPhase);
                return;
            }

            if (succeeded && HasEnded(status))
            {
                IsFinished = true;
                sink.Ended(SessionLaunchRecords.Ended(Identity, status), clean: true);
            }
        }

        public void Refuse(ModErrorCode code, string message)
        {
            if (IsSettled) return;
            IsSettled = true;
            IsFinished = true;
            sink.Result(SessionLaunchRecords.Refused(Identity, code, message ?? string.Empty), null);
        }

        public void Fail(string failure, string detail)
        {
            if (IsSettled) return;
            IsSettled = true;
            IsFinished = true;
            sink.Result(new SessionLaunchResult("failed", new LaunchOutcomeMultiplayer(Identity.Role, Identity.IntentId, null, failure, detail)), reachedPhase);
        }

        /// <summary>Never names the target.</summary>
        public override string ToString() => (IsLiveJoin ? "live join (" : "launched session (") + Identity.Role + ")";

        /// <summary>Gets whether a session that started has ended: hosting stopped, or the join left its session.</summary>
        private bool HasEnded(InternalLiveStatus status) =>
            IsHost
                ? status.HostPhase != InternalHostPhase.Hosting || status.Role != InternalSessionRole.ListenHost
                : status.JoinPhase == InternalJoinPhase.Ended || status.JoinPhase == InternalJoinPhase.None;

        private static bool Same(LaunchProgressSession left, LaunchProgressSession right) =>
            left.State == right.State && left.ServerId == right.ServerId && left.Players == right.Players && left.MaxPlayers == right.MaxPlayers &&
            left.InviteCode == right.InviteCode;
    }
}
