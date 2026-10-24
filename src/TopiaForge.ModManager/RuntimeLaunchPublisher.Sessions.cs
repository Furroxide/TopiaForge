using System;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A session launch's records. The runtime's command (its progress, and a failure before the session started) and the
    /// provider's session (its block, the launch's success or failure, and the session's end) write into the request's files
    /// under one sequence, which follows the runtime's own numbers while only the runtime reports, so neither source's record
    /// can overtake the other's. The runtime's phase stands under the session's block, except while a join connects or
    /// synchronizes. A process that exits after its session launch succeeded writes the session's end as it goes.
    /// </summary>
    internal sealed partial class RuntimeLaunchPublisher : ISessionLaunchSink
    {
        private int sequence = -1;
        private int lastRuntimeSequence = -1;
        private LaunchProgress? runtimeProgress;
        private LaunchProgressSession? sessionBlock;
        private string? sessionPhase;
        private LaunchOutcomeMultiplayer? succeededBlock;

        public bool HasResult
        {
            get
            {
                lock (gate) return outcomes.Contains("launch") || pendingOutcomes.ContainsKey("launch");
            }
        }

        public void Progress(LaunchProgressSession block, string? phase)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            lock (gate)
            {
                if (disposed || session == null) return;
                sessionBlock = block;
                sessionPhase = phase;
                Compose(0);
            }
        }

        public void Result(SessionLaunchResult result, string? phase)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            lock (gate)
            {
                if (disposed || session == null || outcomes.Contains("launch") || pendingOutcomes.ContainsKey("launch")) return;
                var at = Phase(phase, out var sessionId);

                // A success stands at Running with its gamemode session, or at Idle without one. A session that opened while the
                // world was between those, which the realm's rules do not allow, is reported at Idle.
                var succeeded = result.Status == "succeeded";
                if (succeeded && (at != "running" || sessionId == null))
                {
                    at = "idle";
                    sessionId = null;
                }

                var outcome = new LaunchOutcome("launch", session.RequestId, NextSequence(0), at, result.Status, Array.Empty<LaunchBlock>(),
                    succeeded ? sessionId : null, session.Command, result.Error, result.Multiplayer);
                if (succeeded) succeededBlock = result.Multiplayer;
                pendingOutcomes.Add("launch", outcome);
                WritePendingOutcome("launch");
            }
        }

        public void Ended(LaunchOutcomeMultiplayer block, bool clean)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            lock (gate)
            {
                if (disposed || session == null || succeededBlock == null || outcomes.Contains("session") || pendingOutcomes.ContainsKey("session")) return;
                WriteTerminal(block, clean);
            }
        }

        /// <summary>The runtime's progress for a session launch: it moves the phase under the session's block.</summary>
        private void RuntimeProgress(LaunchProgress progress)
        {
            if (progress.Sequence <= lastRuntimeSequence) return;
            lastRuntimeSequence = progress.Sequence;
            runtimeProgress = progress;
            Compose(progress.Sequence);
        }

        private void Compose(int runtimeSequence)
        {
            var block = sessionBlock ?? new LaunchProgressSession(session!.Role, session.IntentId, "pending");
            var phase = Phase(sessionPhase, out var sessionId);
            var nativeBusy = sessionPhase == null ? runtimeProgress?.NativeBusy : null;
            pendingProgress = new LaunchProgress(session!.RequestId, NextSequence(runtimeSequence), phase, sessionId, nativeBusy, block);
            WritePendingProgress();
        }

        /// <summary>The phase a record stands at: a join's own while it connects or synchronizes, otherwise the runtime's.</summary>
        private string Phase(string? joinPhase, out string? sessionId)
        {
            sessionId = null;
            if (joinPhase != null) return joinPhase;
            var runtime = runtimeProgress;
            if (runtime == null) return "idle";
            sessionId = runtime.SessionId;
            return runtime.Phase;
        }

        /// <summary>The process exits with its launched session still running: the session ends as the player left it.</summary>
        private void EndAtExit(bool clean)
        {
            var started = succeededBlock;
            if (session == null || started == null || outcomes.Contains("session") || pendingOutcomes.ContainsKey("session")) return;
            WriteTerminal(new LaunchOutcomeMultiplayer(session.Role, session.IntentId, started.ServerId,
                endReason: session.Role == "host" ? "host-ended" : "left"), clean);
        }

        private void WriteTerminal(LaunchOutcomeMultiplayer block, bool clean)
        {
            var outcome = new LaunchOutcome("session", session!.RequestId, NextSequence(0), "idle", clean ? "succeeded" : "failed",
                Array.Empty<LaunchBlock>(), runtimeProgress?.SessionId, null,
                clean ? null : new LaunchExecutionError("external", "The runtime did not stop cleanly."), block);
            pendingOutcomes.Add("session", outcome);
            WritePendingOutcome("session");
        }

        private int NextSequence(int floor)
        {
            sequence = Math.Max(sequence + 1, floor);
            return sequence;
        }

        private static LaunchOutcome Renumbered(LaunchOutcome outcome, int number) =>
            number == outcome.Sequence
                ? outcome
                : new LaunchOutcome(outcome.Kind, outcome.RequestId, number, outcome.Phase, outcome.Status, outcome.Blocks, outcome.SessionId,
                    outcome.Command, outcome.Error, outcome.Multiplayer);
    }
}
