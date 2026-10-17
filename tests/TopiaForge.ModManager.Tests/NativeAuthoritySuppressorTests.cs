using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The native authority suppressor's bookkeeping: what it turns off, that leaving gives back exactly what it took, and
    /// that a failure while suppressing or restoring never leaves a setting at a value only it imposed.
    /// </summary>
    internal static class NativeAuthoritySuppressorTests
    {
        internal static void Run()
        {
            RestoreIsExact();
            SettingsAlreadyOffAreNeverTouched();
            EachSettingIsSuppressedOnce();
            ChangesMadeSinceAreKept();
            GoneObjectsAreSkipped();
            FailuresWhileSuppressingAreStillUndone();
            FailuresWhileRestoringDoNotStopTheRest();
            ASeededSweepNeverLeavesASuppressedValueBehind();
            AJoinedPlayerTurnsNativeAuthorityOffAndBackOn();
            APartialOrFailedJoinIsUndone();
        }

        private static void RestoreIsExact()
        {
            var (world, switches) = Robots(4);
            var suppressor = new NativeAuthoritySuppressor();
            var before = switches.Select(item => item.Value).ToArray();
            var changed = suppressor.Suppress(Targets(world));
            That(changed == switches.Count, "suppression changes every setting that was on (" + changed + " of " + switches.Count + ")");
            That(switches.All(item => item.Value == item.SuppressedValue), "every native setting takes its suppressed value");
            That(suppressor.CountSuppressed(NativeAuthorityKind.SceneBody) == 4 && suppressor.CountSuppressed(NativeAuthorityKind.RobotBrain) == 8 &&
                 suppressor.CountSuppressed(NativeAuthorityKind.Locomotion) == 4 && suppressor.CountSuppressed(NativeAuthorityKind.Spawner) == 1 &&
                 suppressor.CountSuppressed(NativeAuthorityKind.Pathing) == 1,
                "bodies go kinematic, brains, locomotion, pathing and spawners go off");
            var restore = suppressor.Restore();
            That(switches.Select(item => item.Value).SequenceEqual(before), "restoring gives every setting back exactly the value it had");
            That(restore.Restored == changed && restore.LeftChanged == 0 && restore.Gone == 0 && restore.Failed == 0 && suppressor.SuppressedCount == 0,
                "every change is undone, once (" + restore + ")");
            That(suppressor.Restore().Restored == 0, "restoring again changes nothing");
        }

        private static void SettingsAlreadyOffAreNeverTouched()
        {
            var world = new FakeWorldScene();
            var scene = world.AddScene("Yard");
            var crate = world.Add(scene, "Crate", null, rigidbody: true, kinematic: true);
            var ball = world.Add(scene, "Ball", null, rigidbody: true, kinematic: false);
            var idle = ball.AddNative(NativeAuthorityKind.Spawner, on: false);
            var suppressor = new NativeAuthoritySuppressor();
            That(suppressor.Suppress(Targets(world)) == 1 && crate.KinematicSwitch.WriteCount == 0 && idle.WriteCount == 0 && ball.Kinematic,
                "a setting already at its suppressed value is not written");
            crate.Kinematic = false;
            suppressor.Restore();
            That(!crate.Kinematic && !ball.Kinematic && !idle.Value, "and it is never restored: leaving does not touch what suppression did not change");
        }

        private static void EachSettingIsSuppressedOnce()
        {
            var (world, switches) = Robots(2);
            var suppressor = new NativeAuthoritySuppressor();
            var first = suppressor.Suppress(Targets(world));
            switches[0].SetNatively(!switches[0].SuppressedValue);
            That(suppressor.Suppress(Targets(world)) == 0 && suppressor.SuppressedCount == first,
                "censusing the same scene again suppresses nothing twice, even a setting the game changed back meanwhile");
            var elsewhere = new FakeWorldScene();
            var stranger = elsewhere.Add(elsewhere.AddScene("Elsewhere"), "Stranger", null, rigidbody: true, kinematic: false).KinematicSwitch;
            That(suppressor.IsSuppressed(switches[1]) && !suppressor.IsSuppressed(stranger), "the suppressor knows what it suppressed");
        }

        private static void ChangesMadeSinceAreKept()
        {
            var world = new FakeWorldScene();
            var scene = world.AddScene("Yard");
            var thrown = world.Add(scene, "Thrown", null, rigidbody: true, kinematic: false);
            var spawner = thrown.AddNative(NativeAuthorityKind.Spawner);
            var brain = thrown.AddNative(NativeAuthorityKind.RobotBrain);
            var suppressor = new NativeAuthoritySuppressor();
            suppressor.Suppress(Targets(world));

            // A mod turned the spawner back on during the session; the body was set kinematic again by its own lease.
            spawner.SetNatively(true);
            var restore = suppressor.Restore();
            That(spawner.Value && !thrown.Kinematic && brain.Value && restore.LeftChanged == 1 && restore.Restored == 2,
                "a setting something else changed since keeps that change; the others come back (" + restore + ")");
        }

        private static void GoneObjectsAreSkipped()
        {
            var (world, switches) = Robots(2);
            var suppressor = new NativeAuthoritySuppressor();
            suppressor.Suppress(Targets(world));
            var gone = world.Scenes[0].Roots[0];
            gone.Destroy();
            var writesBefore = switches.Where(item => ReferenceEquals(item.Owner.Root, gone)).Sum(item => item.WriteCount);
            var restore = suppressor.Restore();
            That(restore.Gone > 0 && switches.Where(item => ReferenceEquals(item.Owner.Root, gone)).Sum(item => item.WriteCount) == writesBefore,
                "a destroyed object is skipped, not written (" + restore + ")");
            That(switches.Where(item => item.Owner.IsAlive).All(item => item.Value != item.SuppressedValue), "and everything still there comes back");
        }

        private static void FailuresWhileSuppressingAreStillUndone()
        {
            var world = new FakeWorldScene();
            var scene = world.AddScene("Yard");
            var robot = world.Add(scene, "Robot", null, rigidbody: true, kinematic: false);
            var unreadable = robot.AddNative(NativeAuthorityKind.RobotBrain);
            var unwritable = robot.AddNative(NativeAuthorityKind.Locomotion);
            var fine = robot.AddNative(NativeAuthorityKind.Spawner);
            unreadable.ThrowOnRead = true;
            unwritable.ThrowOnWrite = true;
            var suppressor = new NativeAuthoritySuppressor();
            var changed = suppressor.Suppress(Targets(world));
            That(suppressor.SuppressionFailures == 2 && changed == 3 && robot.Kinematic && !fine.Value && unwritable.Value && unreadable.Value,
                "a read that throws records nothing; a write that throws is still recorded; the targets after them are suppressed (" + changed + ")");
            unreadable.ThrowOnRead = false;
            unwritable.ThrowOnWrite = false;
            var restore = suppressor.Restore();
            That(!robot.Kinematic && fine.Value && unwritable.Value && unreadable.Value && restore.Restored == 2 && restore.Unwritten == 1,
                "restoring undoes what was written, and a write that threw left its setting as it was (" + restore + ")");

            // A write that throws after setting the value, as an engine setter might, is restored all the same.
            var late = world.Add(scene, "Late", null, rigidbody: true, kinematic: false);
            var lying = new LyingSwitch();
            suppressor.Suppress(new[] { new NativeAuthorityTarget(lying, false), new NativeAuthorityTarget(late.KinematicSwitch, true) });
            var second = suppressor.Restore();
            That(lying.Value && !late.Kinematic && second.Restored == 2, "a setting a failed write changed anyway is restored (" + second + ")");
        }

        private static void FailuresWhileRestoringDoNotStopTheRest()
        {
            var (world, switches) = Robots(3);
            var suppressor = new NativeAuthoritySuppressor();
            var before = switches.Select(item => item.Value).ToArray();
            suppressor.Suppress(Targets(world));
            switches[2].ThrowOnWrite = true;
            switches[5].ThrowOnRead = true;
            var restore = suppressor.Restore();
            That(restore.Failed == 2, "two switches throw while restoring (" + restore + ")");
            for (var index = 0; index < switches.Count; index++)
            {
                if (index == 2 || index == 5) continue;
                That(switches[index].Value == before[index], "switch " + index + " is restored though others threw");
            }

            That(suppressor.SuppressedCount == 0, "the ledger is clear afterwards");
        }

        private static void ASeededSweepNeverLeavesASuppressedValueBehind()
        {
            var random = new Random(3602);
            for (var round = 0; round < 300; round++)
            {
                var world = new FakeWorldScene();
                var scene = world.AddScene("Sweep");
                var all = new List<FakeSwitch>();
                for (var item = 0; item < 1 + random.Next(12); item++)
                {
                    var created = world.Add(scene, "O" + item, null, rigidbody: random.Next(2) == 0, kinematic: random.Next(2) == 0);
                    if (created.HasRigidbody) all.Add(created.KinematicSwitch);
                    for (var native = 0; native < random.Next(3); native++) all.Add(created.AddNative((NativeAuthorityKind)(1 + random.Next(4)), random.Next(2) == 0));
                }

                var originals = all.ToDictionary(item => item, item => item.Value);
                var suppressor = new NativeAuthoritySuppressor();
                foreach (var item in all) item.ThrowOnWrite = random.Next(10) == 0;
                suppressor.Suppress(Targets(world));
                foreach (var item in all) item.ThrowOnWrite = false;
                var interfered = new Dictionary<FakeSwitch, bool>();
                foreach (var item in all)
                {
                    if (random.Next(6) != 0) continue;
                    var value = random.Next(2) == 0;
                    item.SetNatively(value);
                    if (value != item.SuppressedValue) interfered[item] = value;
                }

                foreach (var gone in world.Scenes[0].Roots.Where(_ => random.Next(8) == 0).ToList()) gone.Destroy();
                suppressor.Restore();
                foreach (var item in all)
                {
                    if (!item.IsAlive) continue;
                    var expected = interfered.TryGetValue(item, out var kept) ? kept : originals[item];
                    That(item.Value == expected, "round " + round + ": a " + item.Kind + " switch ends at " + expected + ", never at a value only suppression imposed");
                }
            }
        }

        private static void AJoinedPlayerTurnsNativeAuthorityOffAndBackOn()
        {
            var rig = new WorldRig();
            rig.UseScene(scene => Robots(scene, 3));
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("p1");
            var switches = AllSwitches(player.Scene);
            var before = switches.Select(item => item.Value).ToArray();
            rig.JoinAndSynchronize(player);
            That(switches.All(item => item.Value == item.SuppressedValue), "a joined player's native authority is off");
            That(AllSwitches(rig.Host.Scene).Any(item => item.Value != item.SuppressedValue), "and the host's is untouched");
            player.Control.Leave();
            rig.Frames(2);
            That(player.Runtime.Realm == Core.Multiplayer.NetRealm.Offline && switches.Select(item => item.Value).SequenceEqual(before),
                "after leaving, every native setting is back as it was");
            That(player.World.Client.Suppressor.SuppressedCount == 0 && player.Counters().RestoredSwitches == switches.Count,
                "the suppressor holds nothing, and restored exactly what it changed (" + player.Counters().RestoredSwitches + " of " + switches.Count + ")");
        }

        private static void APartialOrFailedJoinIsUndone()
        {
            // The native census throws part way: what it had suppressed by then is undone on leave.
            var rig = new WorldRig();
            var player = rig.AddPlayer("p1");
            Robots(player.Scene, 4);
            var switches = AllSwitches(player.Scene);
            var before = switches.Select(item => item.Value).ToArray();
            player.Scene.FailNativeCensusAfter = 5;
            rig.OpenToMultiplayer();
            rig.Join(player);
            rig.Frames(3);
            That(player.Counters().ActivationFailures == 1 && player.Client.Suppressor.SuppressedCount == 5,
                "a census that fails part way still turns off what it found, and counts the failure (" + player.Client.Suppressor.SuppressedCount + ")");
            player.Control.Leave();
            rig.Frames(2);
            That(switches.Select(item => item.Value).SequenceEqual(before), "and leaving leaves every setting as it was");

            // A join that fails before any world arrives: the player is back in single player, restored and holding nothing.
            player.Scene.FailNativeCensusAfter = null;
            rig.Host.Paused = true;
            rig.Join(player);
            rig.Frames(2);
            That(switches.All(item => item.Value == item.SuppressedValue) && player.Control.Holds == 1, "the player joined, suppressed and holds readiness");
            rig.Network.Leave(player.Control);
            rig.Frames(2);
            That(switches.Select(item => item.Value).SequenceEqual(before) && player.Control.Holds == 0 && !player.World.IsOnline,
                "a join that fails before the world arrives restores everything and lets readiness go");
            rig.Host.Paused = false;
        }

        /// <summary>A scene of robots: each a rigidbody root with a brain, a language model, and locomotion; the first also a spawner and a path follower.</summary>
        internal static (FakeWorldScene World, List<FakeSwitch> Switches) Robots(int count)
        {
            var world = new FakeWorldScene();
            Robots(world, count);
            return (world, AllSwitches(world));
        }

        internal static void Robots(FakeWorldScene world, int count)
        {
            var scene = world.Scenes.Count > 0 ? world.Scenes[0] : world.AddScene("Factory");
            for (var item = 0; item < count; item++)
            {
                var robot = world.Add(scene, "Robot", null, rigidbody: true, kinematic: false);
                robot.AddNative(NativeAuthorityKind.RobotBrain);
                robot.AddNative(NativeAuthorityKind.RobotBrain, on: false, suppressedValue: true);
                robot.AddNative(NativeAuthorityKind.Locomotion);
                if (item == 0)
                {
                    robot.AddNative(NativeAuthorityKind.Spawner);
                    robot.AddNative(NativeAuthorityKind.Pathing);
                }
            }
        }

        internal static List<FakeSwitch> AllSwitches(FakeWorldScene world)
        {
            var all = new List<FakeSwitch>();
            foreach (var item in world.AllObjects())
            {
                if (item.HasRigidbody) all.Add(item.KinematicSwitch);
                all.AddRange(item.Natives);
            }

            return all;
        }

        internal static List<NativeAuthorityTarget> Targets(FakeWorldScene world)
        {
            var targets = new List<NativeAuthorityTarget>();
            world.CollectNativeAuthority(null, targets);
            return targets;
        }

        internal static NetworkWorldCounters Counters(this WorldPeer peer) => peer.World.Counters;

        /// <summary>A switch whose write sets the value and then throws, as an engine setter that reports a warning might.</summary>
        private sealed class LyingSwitch : INativeAuthoritySwitch
        {
            internal bool Value { get; private set; } = true;

            public NativeAuthorityKind Kind => NativeAuthorityKind.Spawner;

            public object Target => this;

            public bool IsAlive => true;

            public bool Read() => Value;

            public void Write(bool value)
            {
                var first = Value == true && value == false;
                Value = value;
                if (first) throw new InvalidOperationException("the setter complained after it set the value");
            }
        }
    }
}
