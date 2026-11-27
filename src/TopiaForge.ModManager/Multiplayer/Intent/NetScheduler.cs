using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The network seam's scheduler (WS3.4). A callback scheduled by code acting on its mod's own player intent captures
    /// that intent there and runs with it each time, while the realm lasts (<see cref="IntentFlow.ScheduledCallback"/>).
    /// That includes work a background task the intent's code started hands back to the main thread, as the SDK tells
    /// background work to (<see cref="IntentFlow.WorkerHandBack"/>). A callback that scripted code or another mod's code
    /// schedules runs with none. Anything else passes through to the scheduler the gameplay host made. A scripted schedule
    /// is handed over exactly as the mod gave it, so it costs nothing more; <see cref="DelayAsync"/> is an await, whose
    /// continuation the execution context already carries.
    /// </summary>
    internal sealed class NetScheduler : IModScheduler
    {
        private readonly IModScheduler inner;

        internal NetScheduler(IModScheduler inner, RuntimeReplication replication)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Replication = replication ?? throw new ArgumentNullException(nameof(replication));
        }

        /// <summary>Gets the scheduler the gameplay host made.</summary>
        internal IModScheduler Inner => inner;

        /// <summary>Gets the replication seam of the context this scheduler belongs to.</summary>
        internal RuntimeReplication Replication { get; }

        public OperationResult<IDisposable> NextFrame(Action action) => inner.NextFrame(Carry(action));

        public OperationResult<IDisposable> After(TimeSpan delay, Action action) => inner.After(delay, Carry(action));

        public OperationResult<IDisposable> Every(TimeSpan interval, Action action) => inner.Every(interval, Carry(action));

        public Task<OperationResult<bool>> DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            inner.DelayAsync(delay, cancellationToken);

        private Action Carry(Action action)
        {
            // A missing action reaches the host's scheduler as it is, which refuses it as before.
            if (action == null) return action!;
            var intents = Replication.Services.Runtime.Intents;
            var captured = intents.CaptureForSchedule(Replication.OwnerModId);
            return captured == null ? action : new ScheduledIntent(intents, captured, action).Run;
        }

        private sealed class ScheduledIntent
        {
            private readonly MultiplayerIntents intents;
            private readonly IntentRecord captured;
            private readonly Action action;

            internal ScheduledIntent(MultiplayerIntents intents, IntentRecord captured, Action action)
            {
                this.intents = intents;
                this.captured = captured;
                this.action = action;
            }

            internal void Run() => intents.RunScheduled(captured, action);
        }
    }
}
