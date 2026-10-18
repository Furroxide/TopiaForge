using System;
using System.Linq;
using System.Reflection;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Intent tracking's rule tables in Core (WS3.4), Unity-free: which entry points carry intent and of which source, and
    /// how intent flows through continuations, threads, scheduled callbacks, other mods' code, realm changes and commands.
    /// </summary>
    internal static class MultiplayerIntentRulesTests
    {
        private static readonly IntentEntryPoint[] PlayerEntries =
        {
            IntentEntryPoint.InputActiveUpdate, IntentEntryPoint.InputActiveLateUpdate, IntentEntryPoint.UiActivation,
            IntentEntryPoint.PlayerCommand, IntentEntryPoint.Interaction, IntentEntryPoint.MotionLease
        };

        private static readonly IntentEntryPoint[] ScriptedEntries =
        {
            IntentEntryPoint.QuietFrame, IntentEntryPoint.FixedUpdate, IntentEntryPoint.ScriptedTimer, IntentEntryPoint.ScriptedCommand,
            IntentEntryPoint.PassiveUi, IntentEntryPoint.RuntimeEvent
        };

        internal static void Run()
        {
            EachSourceHasItsEntryPoint();
            ScriptedEntryPointsCarryNothing();
            FramesAndCommandsChooseTheirEntryPoint();
            FlowsCarryWhatTheTableSays();
            ReadersObserveOnlyTheirOwnIntentOnTheMainThreadInItsEpoch();
            CapturedCallbacksResumeWhileTheirEpochLasts();
            CommandsActForTheSamePlayerAsTheirOwnMod();
            OnlineRealmsRecordAndOnlyHostsActForParticipants();
            RecordsCarryParticipantIdsOnly();
            Console.WriteLine("MultiplayerIntentRulesTests passed.");
        }

        private static void EachSourceHasItsEntryPoint()
        {
            Equal(IntentSource.InputActiveFrame, IntentEntryRules.SourceOf(IntentEntryPoint.InputActiveUpdate), "an input-active Update");
            Equal(IntentSource.InputActiveFrame, IntentEntryRules.SourceOf(IntentEntryPoint.InputActiveLateUpdate), "its LateUpdate");
            Equal(IntentSource.UiCallback, IntentEntryRules.SourceOf(IntentEntryPoint.UiActivation), "a UI control's activation");
            Equal(IntentSource.OwnerCommand, IntentEntryRules.SourceOf(IntentEntryPoint.PlayerCommand), "a command the player runs");
            Equal(IntentSource.InteractionHandler, IntentEntryRules.SourceOf(IntentEntryPoint.Interaction), "an interaction handler");
            Equal(IntentSource.MotionLease, IntentEntryRules.SourceOf(IntentEntryPoint.MotionLease), "the motion lease seam");
            foreach (var entry in PlayerEntries) True(IntentEntryRules.CarriesIntent(entry), entry + " carries the player's intent");

            // Every source but None, and the four the plan names plus the motion lease seam, has an entry point.
            var sources = PlayerEntries.Select(IntentEntryRules.SourceOf).Distinct().OrderBy(source => source).ToArray();
            Sequence(new[] { IntentSource.InputActiveFrame, IntentSource.UiCallback, IntentSource.OwnerCommand, IntentSource.InteractionHandler, IntentSource.MotionLease },
                sources, "every source of player intent has its entry point");
            Throws<ArgumentOutOfRangeException>(() => IntentEntryRules.SourceOf((IntentEntryPoint)12), "an undefined entry point");
            Throws<ArgumentOutOfRangeException>(() => IntentEntryRules.SourceOf((IntentEntryPoint)(-1)), "a negative entry point");
            Equal(PlayerEntries.Length + ScriptedEntries.Length, Enum.GetValues(typeof(IntentEntryPoint)).Length, "the table classifies every entry point");
        }

        private static void ScriptedEntryPointsCarryNothing()
        {
            foreach (var entry in ScriptedEntries)
            {
                Equal(IntentSource.None, IntentEntryRules.SourceOf(entry), entry + " is scripted");
                True(!IntentEntryRules.CarriesIntent(entry), entry + " carries no intent");
            }
        }

        private static void FramesAndCommandsChooseTheirEntryPoint()
        {
            Equal(IntentEntryPoint.InputActiveUpdate, IntentEntryRules.ForUpdate(inputActive: true), "an Update with active input");
            Equal(IntentEntryPoint.QuietFrame, IntentEntryRules.ForUpdate(inputActive: false), "an Update without");
            Equal(IntentEntryPoint.InputActiveLateUpdate, IntentEntryRules.ForLateUpdate(inputActive: true), "a LateUpdate with active input");
            Equal(IntentEntryPoint.QuietFrame, IntentEntryRules.ForLateUpdate(inputActive: false), "a LateUpdate without");
            Equal(IntentEntryPoint.PlayerCommand, IntentEntryRules.ForCommand(runnerActsOnIntent: true), "a command run on intent");
            Equal(IntentEntryPoint.ScriptedCommand, IntentEntryRules.ForCommand(runnerActsOnIntent: false), "a command scripted code runs");
        }

        private static void FlowsCarryWhatTheTableSays()
        {
            Equal(IntentCarriage.Carried, IntentFlowRules.CarriageOf(IntentFlow.AwaitContinuation), "an await's continuation keeps it");
            Equal(IntentCarriage.NotCarried, IntentFlowRules.CarriageOf(IntentFlow.WorkerThread), "a worker thread never observes it");
            Equal(IntentCarriage.CapturedWhenScheduled, IntentFlowRules.CarriageOf(IntentFlow.ScheduledCallback), "a scheduled callback captures it");
            Equal(IntentCarriage.CapturedWhenScheduled, IntentFlowRules.CarriageOf(IntentFlow.DeferredUiCallback), "a deferred UI callback captures it");
            Equal(IntentCarriage.NotCarried, IntentFlowRules.CarriageOf(IntentFlow.NativeCoroutine), "a native coroutine's later steps do not");
            Equal(IntentCarriage.NotCarried, IntentFlowRules.CarriageOf(IntentFlow.EventSubscription), "a subscription carries nothing");
            Equal(IntentCarriage.NotCarried, IntentFlowRules.CarriageOf(IntentFlow.LaterFrame), "later frames never see it");
            Equal(IntentCarriage.NotCarried, IntentFlowRules.CarriageOf(IntentFlow.OtherModCode), "other mods never see it");
            Equal(IntentCarriage.NotCarried, IntentFlowRules.CarriageOf(IntentFlow.RealmChange), "another realm never sees it");
            Equal(IntentCarriage.Reattributed, IntentFlowRules.CarriageOf(IntentFlow.CommandRun), "a command acts for the same player");
            Equal(IntentCarriage.Replaced, IntentFlowRules.CarriageOf(IntentFlow.NestedScope), "a nested scope replaces it for its duration");
            Equal(IntentCarriage.CapturedWhenScheduled, IntentFlowRules.CarriageOf(IntentFlow.WorkerHandBack), "what a worker hands back captures it");
            Equal(12, Enum.GetValues(typeof(IntentFlow)).Length, "the table covers every flow");
            Throws<ArgumentOutOfRangeException>(() => IntentFlowRules.CarriageOf((IntentFlow)12), "an undefined flow");

            // A schedule captures by the mod and the epoch alone, on whichever thread it is made.
            var record = new IntentRecord(IntentSource.UiCallback, "io.example.builder", null, 3);
            True(ReferenceEquals(record, IntentFlowRules.Capture(record, "io.example.builder", 3)), "a schedule of the intent's own mod captures it");
            True(IntentFlowRules.Capture(record, "io.example.neighbour", 3) == null, "another mod's schedule captures none");
            True(IntentFlowRules.Capture(record, "io.example.builder", 4) == null, "nor one after the realm changed");
            True(IntentFlowRules.Capture(null, "io.example.builder", 3) == null, "nor a scripted one");
        }

        private static void ReadersObserveOnlyTheirOwnIntentOnTheMainThreadInItsEpoch()
        {
            var record = new IntentRecord(IntentSource.UiCallback, "io.example.builder", null, 4);
            True(ReferenceEquals(record, IntentFlowRules.Observe(record, "io.example.builder", 4, onOwnerThread: true)), "the mod whose code acts on it observes it");
            True(ReferenceEquals(record, IntentFlowRules.Observe(record, "IO.Example.Builder", 4, onOwnerThread: true)), "mod ids compare as the runtime's do, ignoring case");
            True(IntentFlowRules.Observe(record, "io.example.neighbour", 4, onOwnerThread: true) == null, "another mod's code observes none (OtherModCode)");
            True(IntentFlowRules.Observe(record, "io.example.builder", 4, onOwnerThread: false) == null, "a worker thread observes none (WorkerThread)");
            True(IntentFlowRules.Observe(record, "io.example.builder", 5, onOwnerThread: true) == null, "after the realm changes it is never observed (RealmChange)");
            True(IntentFlowRules.Observe(null, "io.example.builder", 4, onOwnerThread: true) == null, "scripted code has none");
            Throws<ArgumentNullException>(() => IntentFlowRules.Observe(record, null!, 4, true), "a reader names its mod");
        }

        private static void CapturedCallbacksResumeWhileTheirEpochLasts()
        {
            var record = new IntentRecord(IntentSource.InputActiveFrame, "io.example.builder", null, 2);
            True(ReferenceEquals(record, IntentFlowRules.Resume(record, 2)), "a callback scheduled on intent resumes it");
            True(IntentFlowRules.Resume(record, 3) == null, "but not after the realm changed");
            True(IntentFlowRules.Resume(null, 2) == null, "a callback scheduled by scripted code resumes none");
        }

        private static void CommandsActForTheSamePlayerAsTheirOwnMod()
        {
            var local = new IntentRecord(IntentSource.UiCallback, "io.example.console", null, 7);
            var command = IntentFlowRules.ForCommand(local, "io.example.builder", 7);
            True(command != null && command.Source == IntentSource.OwnerCommand && command.ModId == "io.example.builder" && command.IsLocalPlayer && command.Epoch == 7,
                "a command run on the local player's intent acts for that player, as the command's own mod");
            var forwarded = new IntentRecord(IntentSource.InteractionHandler, "io.example.console", "participant-7", 7);
            var forwardedCommand = IntentFlowRules.ForCommand(forwarded, "io.example.builder", 7);
            True(forwardedCommand != null && forwardedCommand.ParticipantId == "participant-7" && !forwardedCommand.IsLocalPlayer,
                "one run for a participant acts for that same participant");
            True(IntentFlowRules.ForCommand(null, "io.example.builder", 7) == null, "a command scripted code runs carries none");
            True(IntentFlowRules.ForCommand(local, "io.example.builder", 8) == null, "nor does one run on an intent from before the realm changed");
            Throws<ArgumentNullException>(() => IntentFlowRules.ForCommand(local, null!, 7), "a command names its mod");
        }

        private static void OnlineRealmsRecordAndOnlyHostsActForParticipants()
        {
            True(!IntentFlowRules.Records(NetRealm.Offline), "offline nothing records: every route is canonical");
            True(IntentFlowRules.Records(NetRealm.ListenHost), "a listen host records");
            True(IntentFlowRules.Records(NetRealm.Client), "a joined player records");
            True(IntentFlowRules.ActsForParticipants(NetRealm.ListenHost), "a listen host acts for the players it forwards for");
            True(!IntentFlowRules.ActsForParticipants(NetRealm.Client), "a joined player acts for nobody else");
            True(!IntentFlowRules.ActsForParticipants(NetRealm.Offline), "nor does single player");
            Throws<ArgumentOutOfRangeException>(() => IntentFlowRules.ActsForParticipants((NetRealm)3), "an undefined realm");
        }

        private static void RecordsCarryParticipantIdsOnly()
        {
            Throws<ArgumentOutOfRangeException>(() => new IntentRecord(IntentSource.None, "io.example.builder", null, 1), "scripted code has no record");
            Throws<ArgumentOutOfRangeException>(() => new IntentRecord((IntentSource)9, "io.example.builder", null, 1), "an undefined source");
            Throws<ArgumentException>(() => new IntentRecord(IntentSource.UiCallback, string.Empty, null, 1), "a record names its mod");
            var control = "part" + (char)1 + "icipant";
            var loneSurrogate = "half" + (char)0xD800;
            foreach (var bad in new[] { string.Empty, " participant", "participant ", control, new string('p', 129), loneSurrogate })
            {
                True(!IntentFlowRules.IsParticipantId(bad), "'" + bad.Length + " characters' is not a participant id");
                Throws<ArgumentException>(() => new IntentRecord(IntentSource.UiCallback, "io.example.builder", bad, 1), "a record refuses it");
            }

            var withPair = "Sam " + char.ConvertFromUtf32(0x1F600);
            True(IntentFlowRules.IsParticipantId(new string('p', 128)) && IntentFlowRules.IsParticipantId(withPair), "1 to 128 printable characters are");
            var record = new IntentRecord(IntentSource.OwnerCommand, "io.example.builder", "participant-3", 1);
            True(!record.ToString().Contains("participant-3", StringComparison.Ordinal), "a record never names its participant when written out (" + record + ")");

            // The record holds the participant's runtime id and nothing shaped like an address.
            var names = typeof(IntentRecord).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Select(member => member.Name.ToLowerInvariant()).ToArray();
            foreach (var word in new[] { "address", "endpoint", "hostname", "socket" })
            {
                True(!names.Any(name => name.Contains(word, StringComparison.Ordinal)), "a record has no member named like '" + word + "'");
            }
        }
    }
}
