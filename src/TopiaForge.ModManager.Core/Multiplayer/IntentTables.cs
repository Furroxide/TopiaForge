using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>Each place the runtime runs mod code, as the intent rules classify it.</summary>
    internal enum IntentEntryPoint
    {
        /// <summary>A mod's Update in a frame where one of its own input actions has an edge or is held, after the input sample.</summary>
        InputActiveUpdate = 0,

        /// <summary>The same mod's LateUpdate in that frame.</summary>
        InputActiveLateUpdate = 1,

        /// <summary>
        /// The player's activation of a TopiaForgeUi control the mod owns: a click or a confirm, a value the player chose,
        /// typed, dragged or submitted, a selection, a close, or a hotkey. The SDK's UI service renders through the kit, so
        /// its callbacks run inside one.
        /// </summary>
        UiActivation = 2,

        /// <summary>A command the player runs, as the chat overlay will, or one that code acting on the player's intent runs.</summary>
        PlayerCommand = 3,

        /// <summary>An interaction handler the local player triggered.</summary>
        Interaction = 4,

        /// <summary>Code driving a motion lease the local player holds; authority routing (WS3.7) attaches it.</summary>
        MotionLease = 5,

        /// <summary>A mod's Update or LateUpdate in a frame where none of its own input actions is active.</summary>
        QuietFrame = 6,

        /// <summary>FixedUpdate. It runs before the frame's input sample, so it never carries the frame's input.</summary>
        FixedUpdate = 7,

        /// <summary>A timer or frame callback that code acting without intent scheduled.</summary>
        ScriptedTimer = 8,

        /// <summary>A command that code acting without intent runs.</summary>
        ScriptedCommand = 9,

        /// <summary>
        /// What only happens to a control: hover, focus, row binding, a theme or accessibility change, and anything code does
        /// to a control itself, such as setting its value or closing it.
        /// </summary>
        PassiveUi = 10,

        /// <summary>Loading and unloading, scene and session events, world events, and every other runtime callback.</summary>
        RuntimeEvent = 11
    }

    /// <summary>The ways intent can travel from the code that acts on it to other code.</summary>
    internal enum IntentFlow
    {
        /// <summary>An await's continuation, and any other work the execution context flows into on the main thread.</summary>
        AwaitContinuation = 0,

        /// <summary>
        /// Code on a worker thread: Task.Run, the thread pool, a timer's thread, or a thread of the mod's own. An await
        /// that comes back to the main thread observes the intent again, and so does what the worker hands back
        /// (<see cref="WorkerHandBack"/>).
        /// </summary>
        WorkerThread = 1,

        /// <summary>A callback scheduled through the SDK scheduler: NextFrame, After and Every, and a coroutine stepped through them.</summary>
        ScheduledCallback = 2,

        /// <summary>A callback the UI kit defers, such as a window's close animation finishing.</summary>
        DeferredUiCallback = 3,

        /// <summary>A native Unity coroutine's steps after its first, which Unity runs from its own loop.</summary>
        NativeCoroutine = 4,

        /// <summary>A frame or scene event subscription made inside a scope.</summary>
        EventSubscription = 5,

        /// <summary>The frames after the one the scope opened in.</summary>
        LaterFrame = 6,

        /// <summary>Another mod's code run from inside the scope: its callbacks, the services it published, and their continuations.</summary>
        OtherModCode = 7,

        /// <summary>Anything after the realm changes: hosting, joining, leaving or stopping.</summary>
        RealmChange = 8,

        /// <summary>A command run from inside the scope.</summary>
        CommandRun = 9,

        /// <summary>A scope opened inside another.</summary>
        NestedScope = 10,

        /// <summary>
        /// Work a worker thread hands back to the main thread through the SDK scheduler, as the SDK tells background work to,
        /// when the intent's own code started that worker.
        /// </summary>
        WorkerHandBack = 11
    }

    /// <summary>What a flow does with the intent.</summary>
    internal enum IntentCarriage
    {
        /// <summary>The intent reaches the code, which acts on it as the code that started it does.</summary>
        Carried = 0,

        /// <summary>The intent is captured where the work is scheduled, on the main thread, and restored each time it runs.</summary>
        CapturedWhenScheduled = 1,

        /// <summary>The code acts for the same player, as the mod whose code it is: a command's handler.</summary>
        Reattributed = 2,

        /// <summary>The inner scope's intent flows for its duration, and the outer one's again after it.</summary>
        Replaced = 3,

        /// <summary>The code observes no intent.</summary>
        NotCarried = 4
    }

    /// <summary>Which entry points carry intent, and of which source: the first rule table of intent tracking (WS3.4).</summary>
    internal static class IntentEntryRules
    {
        // Indexed by IntentEntryPoint. Only the player's own actions carry intent; everything else is scripted.
        private static readonly IntentSource[] Sources =
        {
            IntentSource.InputActiveFrame,   // InputActiveUpdate
            IntentSource.InputActiveFrame,   // InputActiveLateUpdate
            IntentSource.UiCallback,         // UiActivation
            IntentSource.OwnerCommand,       // PlayerCommand
            IntentSource.InteractionHandler, // Interaction
            IntentSource.MotionLease,        // MotionLease
            IntentSource.None,               // QuietFrame
            IntentSource.None,               // FixedUpdate
            IntentSource.None,               // ScriptedTimer
            IntentSource.None,               // ScriptedCommand
            IntentSource.None,               // PassiveUi
            IntentSource.None                // RuntimeEvent
        };

        public static bool IsDefined(IntentEntryPoint entry) =>
            entry >= IntentEntryPoint.InputActiveUpdate && entry <= IntentEntryPoint.RuntimeEvent;

        /// <summary>The source of the local player's intent an entry point carries; <see cref="IntentSource.None"/> when it is scripted.</summary>
        public static IntentSource SourceOf(IntentEntryPoint entry)
        {
            if (!IsDefined(entry)) throw new ArgumentOutOfRangeException(nameof(entry));
            return Sources[(int)entry];
        }

        /// <summary>Whether an entry point carries the player's intent.</summary>
        public static bool CarriesIntent(IntentEntryPoint entry) => SourceOf(entry) != IntentSource.None;

        /// <summary>A mod's Update after the input sample: input-active when one of its own actions has an edge or is held.</summary>
        public static IntentEntryPoint ForUpdate(bool inputActive) =>
            inputActive ? IntentEntryPoint.InputActiveUpdate : IntentEntryPoint.QuietFrame;

        /// <summary>A mod's LateUpdate in the same frame, by the same test.</summary>
        public static IntentEntryPoint ForLateUpdate(bool inputActive) =>
            inputActive ? IntentEntryPoint.InputActiveLateUpdate : IntentEntryPoint.QuietFrame;

        /// <summary>A command's run: the player's when the code running it acts on player intent, and scripted otherwise.</summary>
        public static IntentEntryPoint ForCommand(bool runnerActsOnIntent) =>
            runnerActsOnIntent ? IntentEntryPoint.PlayerCommand : IntentEntryPoint.ScriptedCommand;
    }

    /// <summary>
    /// How intent flows: the second rule table of intent tracking (WS3.4), and the decisions that apply it. Intent flows
    /// into the continuations the intent's own code starts, and never into later frames, other mods' code, scripted timers
    /// or another realm.
    /// </summary>
    internal static class IntentFlowRules
    {
        /// <summary>The longest participant id a record holds, as the replication seam bounds a command's sender.</summary>
        internal const int MaximumParticipantIdLength = 128;

        // Indexed by IntentFlow.
        private static readonly IntentCarriage[] Carriages =
        {
            IntentCarriage.Carried,               // AwaitContinuation
            IntentCarriage.NotCarried,            // WorkerThread
            IntentCarriage.CapturedWhenScheduled, // ScheduledCallback
            IntentCarriage.CapturedWhenScheduled, // DeferredUiCallback
            IntentCarriage.NotCarried,            // NativeCoroutine
            IntentCarriage.NotCarried,            // EventSubscription
            IntentCarriage.NotCarried,            // LaterFrame
            IntentCarriage.NotCarried,            // OtherModCode
            IntentCarriage.NotCarried,            // RealmChange
            IntentCarriage.Reattributed,          // CommandRun
            IntentCarriage.Replaced,              // NestedScope
            IntentCarriage.CapturedWhenScheduled  // WorkerHandBack
        };

        public static bool IsDefined(IntentFlow flow) => flow >= IntentFlow.AwaitContinuation && flow <= IntentFlow.WorkerHandBack;

        /// <summary>What <paramref name="flow"/> does with the intent.</summary>
        public static IntentCarriage CarriageOf(IntentFlow flow)
        {
            if (!IsDefined(flow)) throw new ArgumentOutOfRangeException(nameof(flow));
            return Carriages[(int)flow];
        }

        /// <summary>
        /// Whether a realm records intent. Online realms do. Offline every route is canonical whatever the intent, so no
        /// scope opens and an offline frame costs one field read.
        /// </summary>
        public static bool Records(NetRealm realm) => NetRealms.IsOnline(realm);

        /// <summary>Whether code in a realm may act for another participant: only a listen host runs what its players forward.</summary>
        public static bool ActsForParticipants(NetRealm realm)
        {
            NetRealms.Require(realm);
            return realm == NetRealm.ListenHost;
        }

        /// <summary>
        /// What <paramref name="readerModId"/>'s code observes of what flows to it: the record, on the main thread, while
        /// its epoch lasts, when the record belongs to that mod. A worker thread, another mod and a later realm observe none.
        /// </summary>
        public static IntentRecord? Observe(IntentRecord? flowing, string readerModId, long epoch, bool onOwnerThread)
        {
            if (readerModId == null) throw new ArgumentNullException(nameof(readerModId));
            if (flowing == null || !onOwnerThread) return null;
            if (flowing.Epoch != epoch) return null;
            return string.Equals(flowing.ModId, readerModId, StringComparison.OrdinalIgnoreCase) ? flowing : null;
        }

        /// <summary>
        /// What a callback that <paramref name="schedulingModId"/>'s code schedules through the SDK scheduler captures, on
        /// whichever thread it is scheduled: the mod's own record, from this epoch, as the execution context carries it there
        /// (<see cref="IntentFlow.ScheduledCallback"/>, <see cref="IntentFlow.WorkerHandBack"/>). A worker never observes the
        /// record, but the work it hands back to the main thread is the intent's own continuation. Another mod's record, an
        /// earlier realm's and scripted code capture none.
        /// </summary>
        public static IntentRecord? Capture(IntentRecord? flowing, string schedulingModId, long epoch) =>
            Observe(flowing, schedulingModId, epoch, onOwnerThread: true);

        /// <summary>
        /// The record a captured callback runs with: the one captured where it was scheduled, while that epoch lasts. After
        /// a change of realm it runs with none.
        /// </summary>
        public static IntentRecord? Resume(IntentRecord? captured, long epoch) =>
            captured != null && captured.Epoch == epoch ? captured : null;

        /// <summary>
        /// The intent a command's handler acts on: the same player's, as an owner command of the command's own mod, when the
        /// code that ran it observed player intent; none otherwise. A command an input-active frame runs stays an input
        /// frame's (<see cref="IntentRecord.IsInputFrame"/>), so it never passes for the player's own command.
        /// </summary>
        public static IntentRecord? ForCommand(IntentRecord? runner, string commandModId, long epoch)
        {
            if (commandModId == null) throw new ArgumentNullException(nameof(commandModId));
            if (runner == null || runner.Epoch != epoch) return null;
            return new IntentRecord(IntentSource.OwnerCommand, commandModId, runner.ParticipantId, epoch, runner.IsInputFrame);
        }

        /// <summary>
        /// Whether a TopiaForgeUi control's event belongs to an input-active frame rather than to the player's click. The
        /// player's own input reaches a control from the UI's event loop, outside every mod's code, so an event raised while
        /// an input frame's intent flows, whichever mod's, was fired by that frame's code: it keeps the frame's coarse
        /// intent (<see cref="IntentRecord.IsInputFrame"/>) and never passes for a click. An intent from before the realm
        /// changed is not observed, so it changes nothing.
        /// </summary>
        public static bool UiActivationIsInputFrame(IntentRecord? flowing, long epoch) =>
            flowing != null && flowing.Epoch == epoch && flowing.IsInputFrame;

        /// <summary>A participant id: 1 to 128 UTF-16 units, no surrounding white space, no control characters and no lone surrogates.</summary>
        public static bool IsParticipantId(string? value)
        {
            if (value == null || value.Length == 0 || value.Length > MaximumParticipantIdLength) return false;
            if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1])) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsControl(character)) return false;
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
