using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The runtime's intent tracking (WS3.4): the <see cref="IntentScope"/> and the realm epoch it records against, and the
    /// entry points that open it. Online, a player's own action opens a scope around the mod code that runs for it: an
    /// input-active frame, a UI control's activation, a command the player runs and an interaction. Offline nothing opens,
    /// since every route is canonical whatever the intent. The rules it applies are Core's <see cref="IntentEntryRules"/>
    /// and <see cref="IntentFlowRules"/>; docs/internal/MultiplayerRuntimeIntegration.md, section 14, states them. Main
    /// thread only: code on any other thread observes no intent.
    /// </summary>
    internal sealed class MultiplayerIntents
    {
        /// <summary>At most this many UI owners keep a cached record; more are made per activation.</summary>
        internal const int MaximumCachedControlOwners = 256;

        private static readonly ContextCallback InvokeAction = state => ((Action)state!)();
        private readonly Dictionary<string, IntentRecord> controls = new Dictionary<string, IntentRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IntentRecord> inputFrameControls = new Dictionary<string, IntentRecord>(StringComparer.OrdinalIgnoreCase);
        private NetRealm realm;
        private bool recording;
        private long epoch;

        internal MultiplayerIntents()
            : this(new IntentScope())
        {
        }

        internal MultiplayerIntents(IntentScope scope)
        {
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        }

        /// <summary>Gets the scope every entry point opens.</summary>
        internal IntentScope Scope { get; }

        /// <summary>Gets whether intent is recorded now: online, as the last pump showed the realm. One field read.</summary>
        internal bool IsRecording => recording;

        /// <summary>Gets the realm epoch: it moves on each change of realm, and an intent of an earlier one is never observed.</summary>
        internal long Epoch => epoch;

        /// <summary>Gets the realm as the runtime last reported it.</summary>
        internal NetRealm Realm => realm;

        /// <summary>The manager log's line for how intent tracking runs its records on this runtime (<see cref="IntentScopeMode"/>).</summary>
        internal static string DescribeMode(IntentScopeMode mode)
        {
            switch (mode)
            {
                case IntentScopeMode.ReusedContext:
                    return "Multiplayer: intent tracking runs on reused execution contexts; an input-active frame allocates nothing.";
                case IntentScopeMode.LogicalCallContext:
                    return "Multiplayer: intent tracking runs on the logical call context; an input-active frame allocates nothing.";
                default:
                    return "Multiplayer: intent tracking runs on its async-local fallback, since this runtime passed neither " +
                           "allocation-free way at startup; each input-active frame allocates while online.";
            }
        }

        /// <summary>
        /// Follows the realm the composition root moved to. Each change starts a new epoch, so the continuations of an intent
        /// from before hosting, joining or leaving observe none, and online realms record.
        /// </summary>
        internal void FollowRealm(NetRealm next)
        {
            NetRealms.Require(next);
            if (next == realm) return;
            realm = next;
            Interlocked.Increment(ref epoch);
            Volatile.Write(ref recording, IntentFlowRules.Records(next));
            controls.Clear();
            inputFrameControls.Clear();
        }

        /// <summary>What <paramref name="modId"/>'s code observes now: its own intent, on the main thread, from this epoch; else null.</summary>
        internal IntentRecord? VisibleTo(string modId) => IntentFlowRules.Observe(Scope.Flowing, modId, epoch, Scope.IsOwnerThread);

        /// <summary>
        /// Gets the local player's record of <paramref name="source"/> for <paramref name="modId"/> in this epoch, kept in
        /// <paramref name="slot"/> so the steady state reuses it.
        /// </summary>
        internal IntentRecord LocalRecord(ref IntentRecord? slot, string modId, IntentSource source)
        {
            var record = slot;
            if (record == null || record.Epoch != epoch || record.Source != source || !string.Equals(record.ModId, modId, StringComparison.Ordinal))
            {
                record = new IntentRecord(source, modId, null, epoch);
                slot = record;
            }

            return record;
        }

        /// <summary>
        /// Runs a mod's frame callback for an input-active frame: its Update, or with <paramref name="late"/> its LateUpdate,
        /// with the local player's input intent flowing to it. A quiet frame, or offline, runs it as it is.
        /// </summary>
        internal void RunInputFrame(ref IntentRecord? slot, string modId, bool late, ContextCallback callback, object state)
        {
            var entry = late ? IntentEntryRules.ForLateUpdate(inputActive: true) : IntentEntryRules.ForUpdate(inputActive: true);
            var source = IntentEntryRules.SourceOf(entry);
            if (!recording || source == IntentSource.None)
            {
                callback(state);
                return;
            }

            Scope.Run(LocalRecord(ref slot, modId, source), callback, state);
        }

        /// <summary>
        /// Opens the local player's UI intent for a TopiaForgeUi control's activation, attributed to the control's owner: the
        /// kit's hook calls it, and the SDK UI service's callbacks run inside it. An event an input-active frame's code fires
        /// while that frame's intent flows stays coarse (<see cref="IntentRecord.IsInputFrame"/>), since the player's own
        /// input never reaches a control from inside mod code.
        /// </summary>
        internal IntentScope.Token EnterUiActivation(string ownerId)
        {
            if (!recording || string.IsNullOrEmpty(ownerId)) return default;
            var fromInputFrame = IntentFlowRules.UiActivationIsInputFrame(Scope.Flowing, epoch);
            var cache = fromInputFrame ? inputFrameControls : controls;
            if (!cache.TryGetValue(ownerId, out var record))
            {
                record = new IntentRecord(IntentEntryRules.SourceOf(IntentEntryPoint.UiActivation), ownerId, null, epoch, fromInputFrame);
                if (cache.Count < MaximumCachedControlOwners) cache.Add(ownerId, record);
            }

            return Scope.Enter(record);
        }

        /// <summary>
        /// Opens what a command's handler acts on (<see cref="IntentFlow.CommandRun"/>): the player's intent the code that ran
        /// it observed, as an owner command of the command's own mod, or none for a scripted run. A scripted run clears what
        /// else flows, so a command never borrows another mod's intent through the mod that ran it.
        /// </summary>
        internal IntentScope.Token EnterCommand(string commandModId, IntentRecord? runner)
        {
            if (!recording) return default;
            var record = IntentFlowRules.ForCommand(runner, commandModId, epoch);
            return record != null || Scope.Flowing != null ? Scope.Enter(record) : default;
        }

        /// <summary>Opens the local player's own command (the chat overlay's, WS5) for the command's mod.</summary>
        internal IntentScope.Token EnterPlayerCommand(string commandModId)
        {
            if (!recording) return default;
            return Scope.Enter(new IntentRecord(IntentEntryRules.SourceOf(IntentEntryPoint.PlayerCommand), commandModId, null, epoch));
        }

        /// <summary>Runs an interaction handler the local player triggered, with its intent flowing to it.</summary>
        internal void RunInteraction(ref IntentRecord? slot, string modId, ContextCallback callback, object state)
        {
            if (!recording)
            {
                callback(state);
                return;
            }

            Scope.Run(LocalRecord(ref slot, modId, IntentEntryRules.SourceOf(IntentEntryPoint.Interaction)), callback, state);
        }

        /// <summary>
        /// What a callback <paramref name="modId"/>'s code schedules now captures (<see cref="IntentFlow.ScheduledCallback"/>):
        /// its own intent, on whichever thread it schedules, since work a worker hands back to the main thread is the intent's
        /// own continuation (<see cref="IntentFlow.WorkerHandBack"/>); null for scripted code, another mod's intent, or
        /// offline. A worker may call it, so it reads only fields the main thread writes whole.
        /// </summary>
        internal IntentRecord? CaptureForSchedule(string modId) =>
            Volatile.Read(ref recording) ? IntentFlowRules.Capture(Scope.FlowingOnAnyThread, modId, Interlocked.Read(ref epoch)) : null;

        /// <summary>Runs a scheduled callback with what was captured where it was scheduled, while that epoch lasts.</summary>
        internal void RunScheduled(IntentRecord captured, Action action)
        {
            var record = recording ? IntentFlowRules.Resume(captured, epoch) : null;
            if (record == null && Scope.Flowing == null)
            {
                action();
                return;
            }

            Scope.Run(record, InvokeAction, action);
        }

        /// <summary>
        /// Captures what flows to the calling code for a callback the UI kit defers (<see cref="IntentFlow.DeferredUiCallback"/>).
        /// The record keeps its own mod, so only that mod's code observes it when the callback runs.
        /// </summary>
        internal IntentRecord? CaptureDeferred() => recording ? Scope.Flowing : null;

        /// <summary>Re-enters what <see cref="CaptureDeferred"/> captured, while that epoch lasts; otherwise what flows is cleared.</summary>
        internal IntentScope.Token EnterDeferred(IntentRecord? captured)
        {
            var record = recording ? IntentFlowRules.Resume(captured, epoch) : null;
            return record != null || Scope.Flowing != null ? Scope.Enter(record) : default;
        }

        /// <summary>
        /// The motion lease seam (WS3.7): opens the local player's motion-lease intent for <paramref name="modId"/> around the
        /// code driving a lease that player holds. Nothing calls it until authority routing lands.
        /// </summary>
        internal IntentScope.Token EnterMotionLease(string modId)
        {
            if (modId == null) throw new ArgumentNullException(nameof(modId));
            if (!recording) return default;
            return Scope.Enter(new IntentRecord(IntentEntryRules.SourceOf(IntentEntryPoint.MotionLease), modId, null, epoch));
        }

        /// <summary>
        /// The forwarded-request seam (WS3.7): on a listen host, opens a participant's intent of <paramref name="source"/>
        /// for <paramref name="modId"/> around the code that carries out what that participant's process forwarded. It names
        /// the participant by the id the host gave it, never by an address. A joined player never acts for another
        /// participant, so there it opens nothing. <paramref name="fromInputFrame"/> says the participant's process made the
        /// request from an input-active frame (<see cref="IntentRecord.IsInputFrame"/>), as a command such a frame ran.
        /// </summary>
        internal IntentScope.Token EnterForwarded(string modId, IntentSource source, string participantId, bool fromInputFrame = false)
        {
            if (modId == null) throw new ArgumentNullException(nameof(modId));
            if (!IntentRules.IsPlayerIntent(source)) throw new ArgumentOutOfRangeException(nameof(source), "A forwarded request carries player intent.");
            if (!IntentFlowRules.IsParticipantId(participantId)) throw new ArgumentException("A participant id is 1 to 128 printable characters.", nameof(participantId));
            if (!recording || !IntentFlowRules.ActsForParticipants(realm)) return default;
            return Scope.Enter(new IntentRecord(source, modId, participantId, epoch, fromInputFrame));
        }
    }
}
