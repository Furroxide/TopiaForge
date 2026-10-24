using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class MotionAbuseTests
    {
        internal static void Run()
        {
            WrongOwnersEpochsAndFloodsCannotMoveBodies();
            RestartedStreamsRetireTheirLeases();
            ResetDoesNotRewindFreeBodies();
            AGrabRefusedForItsBudgetIsAnsweredAtOnce();
            ManyHoldersShareTheStateBudgetInTurns();
        }

        private static void WrongOwnersEpochsAndFloodsCannotMoveBodies()
        {
            var rig = AuthorityRig.Create(boxes: 2);
            var first = rig.Players[0];
            var second = rig.Players[1];
            var lease = MotionTests.Grab(first);
            var other = MotionTests.Grab(second, 1);
            rig.Frames(8);
            var body = MotionTests.Box(rig.Host);
            var message = new MotionMessage
            {
                Target = lease.Target,
                Epoch = lease.Epoch,
                Request = lease.Request,
                Pose = new NetPose(new NetVector3(2, 0, 0), NetQuaternion.Identity),
                PlayerPosition = default
            };
            Send(second, MotionMessages.Sample);
            rig.Frames(4);
            That(body.Position.X == 0 && lease.IsAlive, "a different sender cannot write a valid epoch's body");
            message.Epoch--;
            Send(first, MotionMessages.Handback);
            rig.Frames(4);
            That(body.Position.X == 0 && lease.IsAlive, "an old epoch cannot hand back or move a current hold");
            message.Epoch = lease.Epoch;
            message.Pose = new NetPose(default, NetQuaternion.Identity);
            var accepted = rig.Host.World.Motion.SamplesAccepted;
            for (var index = 0; index < 500; index++) Send(first, MotionMessages.Sample);
            rig.Frames(4);
            var delta = rig.Host.World.Motion.SamplesAccepted - accepted;
            That(delta < 400 && lease.IsAlive && other.IsAlive, "a raw sample flood is bounded by its sender's byte bucket and does not consume another sender's budget");

            void Send(WorldPeer from, byte type)
            {
                var writer = new WorldWireWriter();
                MotionWire.Write(writer, type, message);
                SpawnAbuseTests.Inject(rig, from, writer.Segment.ToArray());
            }
        }

        private static void RestartedStreamsRetireTheirLeases()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 1);
            var lease = MotionTests.Grab(rig.Players[0]);
            rig.Frames(8);
            rig.Network.Rejoin(rig.Players[0].Control);
            rig.Frames(2);
            That(lease.Ended && MotionTests.Box(rig.Host).Kinematic && MotionTests.HostHas(rig, lease),
                "a restarted stream loses its old ownership on both sides immediately");
        }

        private static void ResetDoesNotRewindFreeBodies()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 1);
            var lease = MotionTests.Grab(rig.Players[0]);
            rig.Frames(8);
            lease.Dispose();
            rig.Frames(8);
            var body = MotionTests.Box(rig.Host);
            body.Position = new NetVector3(8, 0, 0);
            rig.Host.World.Motion.Reset();
            That(body.Position.X == 8, "retiring motion history never rewinds a body already back under server physics");
        }

        private static void AGrabRefusedForItsBudgetIsAnsweredAtOnce()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 2);
            var player = rig.Players[0];
            var mod = new AuthorityMod(player);
            var warm = MotionTests.Grab(player, 1, mod);
            rig.Frames(8);
            warm.Dispose();
            rig.Frames(140);

            // Past its grab budget a player is refused before the host looks at the body, so the answer names no epoch,
            // while the player knows a later one for this body from its own last hold.
            var flood = new MotionMessage
            {
                Target = new MotionTarget(new SceneObjectKey(0x1234_5678_9ABCUL)),
                Request = 50_000,
                Pose = new NetPose(NetVector3.Zero, NetQuaternion.Identity)
            };
            for (var index = 0; index < 12; index++)
            {
                var writer = new WorldWireWriter();
                MotionWire.Write(writer, MotionMessages.Grab, flood);
                SpawnAbuseTests.Inject(rig, player, writer.Segment.ToArray());
            }

            var refused = rig.Host.World.Motion.GrabsRefused;
            var lease = MotionTests.Grab(player, 1, mod);
            rig.Frames(8);
            That(rig.Host.World.Motion.GrabsRefused > refused + 12 && lease.Ended && mod.Ui.Toasts.Count == 1,
                "a grab refused for its budget ends at once with one toast, rather than simulating alone until the answer timeout");
        }

        private static void ManyHoldersShareTheStateBudgetInTurns()
        {
            var rig = AuthorityRig.Create(players: 2, boxes: 8);
            var leases = new List<NetEntityMotion>();
            for (var index = 0; index < 8; index++) leases.Add(MotionTests.Grab(rig.Players[index / 4], index));
            rig.Frames(8);
            var states = new List<HostedMotion>();
            foreach (var lease in leases)
            {
                var tracked = rig.Host.World.Motion.TryGetHosted(lease.Target.Id, out var state);
                That(lease.Granted && tracked, "every one of the eight holds is granted");
                states.Add(state);
            }

            // Eight bodies at 30 Hz need more than the host's motion budget, so once its burst is spent they take turns.
            Move(300);
            var before = states.Select(state => state.Broadcasts).ToArray();
            Move(625);
            var sent = states.Select((state, index) => state.Broadcasts - before[index]).ToArray();
            That(leases.All(lease => lease.IsAlive) && sent.Min() >= 120,
                "every held body keeps its share of state broadcasts when the budget cannot cover all (" + string.Join(", ", sent) + " in 10 s)");

            void Move(int frames)
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    var t = rig.Clock.Now / 1_000_000.0;
                    for (var index = 0; index < 8; index++)
                        MotionTests.Box(rig.Players[index / 4], index).Position = new NetVector3(index * 2 + (float)Math.Sin(t) * 1.5f, 0, 0);
                    rig.Frame();
                }
            }
        }
    }
}
