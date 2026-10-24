using System;
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
            That(lease.Ended && MotionTests.Box(rig.Host).Kinematic, "a restarted stream loses its old ownership on both sides immediately");
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
    }
}
