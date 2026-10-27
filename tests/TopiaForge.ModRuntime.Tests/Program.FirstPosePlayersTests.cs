using System;
using System.IO;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestFirstPosePlayersAcrossRuntimes(string root)
        {
            UnityMainThreadGuard.CaptureCurrentThread();
            foreach (var flags in new[] { PresenceFlags.None, PresenceFlags.Teleported })
            {
                using var test = new FirstPosePlayersGame(Path.Combine(root, "placement-" + flags));
                test.Pose(900, flags);
                test.AssertAbsent("no running world supplies no invented readiness");
                Assert(test.Grab(900).Result == OwnershipResult.NoChange, "missing trust cannot grant a body near the packet");
                Assert(test.Player.Teleport(new Vec3(900, 0, 0)).Succeeded, "Players sends a host placement before the first pose");
                test.AssertAbsent("sending a placement does not itself validate a pose");
                test.Pose(900, flags);
                Assert(test.Player.Entity != null && test.Player.TryGetSnapshot(out var snapshot) && snapshot!.Position.X == 900,
                    "a first host landing reaches the real Players handle with either flag");
                Assert(test.Grab(900).Result == OwnershipResult.Granted, "a trusted host landing grants legitimate nearby motion");
                test.Clock.Now += PresencePolicy.DefaultStaleAfterMicros + 1;
                Assert(test.Host.Runtime.Presence.ValidatedPosition(test.Raw.Id) == null, "host placement does not disable freshness");
                Assert(test.Grab(900, 2).Result == OwnershipResult.NoChange, "an expired validated anchor supplies no range authority");
                test.Pose(1100, PresenceFlags.Teleported);
                test.Pose(1300, PresenceFlags.Teleported);
                Assert(test.Position?.X == 1300, "host placement spends neither own teleport credit");
                test.Pose(1500, PresenceFlags.Teleported);
                Assert(test.Position?.X == 1300 && test.Peer.Plausibility!.Strikes == 1, "the third own teleport is still refused");
            }

            foreach (var late in new[] { false, true })
            {
                using var test = new FirstPosePlayersGame(Path.Combine(root, "deadline-" + late));
                Assert(test.Player.Teleport(new Vec3(900, 0, 0)).Succeeded, "deadline placement is sent through Players");
                // Flush independently: the host processes the landing at exactly the selected host-clock deadline.
                test.Clock.Now += 10_000_000 + (late ? 1 : 0);
                test.Pose(900, PresenceFlags.Teleported);
                if (late) test.AssertAbsent("an expired first host landing creates no avatar, public snapshot or range authority");
                else Assert(test.Position?.X == 900, "a first host landing at the ten-second deadline is accepted");
            }

            using (var failed = new FirstPosePlayersGame(Path.Combine(root, "failed-send")))
            {
                failed.Host.Runtime.Replication.Players!.Channel!.Dispose();
                Assert(!failed.Player.Teleport(new Vec3(900, 0, 0)).Succeeded, "a closed Players stream refuses the host action");
                failed.Pose(900, PresenceFlags.Teleported);
                failed.AssertAbsent("a failed Players send cannot establish trust");
            }

            using (var reset = new FirstPosePlayersGame(Path.Combine(root, "scene-reset")))
            {
                Assert(reset.Player.Teleport(new Vec3(900, 0, 0)).Succeeded, "old scene placement is sent");
                reset.Pose(900);
                var oldAvatar = reset.Peer.Avatar;
                Assert(oldAvatar != null, "the old scene had an avatar");
                Assert(reset.Player.Teleport(new Vec3(1100, 0, 0)).Succeeded, "a second old scene landing is pending");
                reset.Host.LoadScene("Next", true);
                Assert(reset.Position == null, "native scene notification invalidates motion before receive");
                reset.Pose(1100, PresenceFlags.Teleported);
                reset.AssertAbsent("late old-scene landing cannot reuse the previous placement");
                Assert(!oldAvatar!.IsAlive, "the old avatar is retired");
                reset.Host.LoadScene("Last", true);
                Assert(reset.Player.Teleport(new Vec3(1200, 0, 0)).Succeeded, "a host placement just after scene notification is sent");
                reset.Pose(1200);
                Assert(reset.Position?.X == 1200 && reset.Player.Entity != null,
                    "a new-scene Players placement before presence receive survives world invalidation");
            }
            Console.WriteLine("First-pose Players runtime passed: 6 cases; native readiness absent, real host actions, deadlines, motion and scene invalidation.");
        }

        private sealed class FirstPosePlayersGame : IDisposable
        {
            internal readonly WorldTestClock Clock = new WorldTestClock();
            internal readonly WorldGame Host;
            internal readonly LoopbackControl Raw;
            private readonly IInternalSystemChannel avatars;
            private readonly IInternalSystemChannel world;
            private readonly PlayersTestServices services;
            internal readonly IPlayer Player;

            internal FirstPosePlayersGame(string root)
            {
                var network = new LoopbackNetPair();
                Host = new WorldGame(NewFixture(root, "presence", PresenceModEntry), network.Host, Clock);
                Host.Game.Presence.HasPlayer = true;
                Host.Start();
                Host.LoadScene("Yard", true);
                // Keep ModRuntime's real readiness and scene delegates: this native scene has no world session.
                Assert(Host.Runtime.Presence.RunningWorld!() == null && Host.Runtime.Presence.RunningSpawn!() == null,
                    "the real orchestrator wiring supplies no readiness outside a world session");
                services = new PlayersTestServices(Host.Runtime.Replication.Players!, Host.Control, Host.Game.Presence);
                Assert(Host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("First pose", 4)).Succeeded, "host opens");
                WorldFrames(new[] { Host }, Clock, 3);
                Raw = network.AddPlayer("raw");
                avatars = Raw.OpenSystemChannel(InternalSystemChannelKind.Avatars).Value!;
                world = Raw.OpenSystemChannel(InternalSystemChannelKind.WorldReplication).Value!;
                Raw.OpenSystemChannel(InternalSystemChannelKind.Players);
                network.Join(Raw);
                Frames(3);
                Player = services.Player(Raw.Id);
            }

            internal PresencePeer Peer
            {
                get { Assert(Host.Runtime.Presence.TryGetPeer(Raw.Id, out var peer), "raw peer is present"); return peer!; }
            }
            internal NetVector3? Position => Host.Runtime.Presence.ValidatedPosition(Raw.Id);
            private void Frames(int count)
            {
                for (var i = 0; i < count; i++) { Raw.Pump(); Host.Frame(); Raw.Flush(); Clock.Now += 16_000; }
            }
            internal void Pose(float x, PresenceFlags flags = PresenceFlags.None)
            {
                var bits = new NetBitWriter(PresenceMessages.PoseBytes);
                PresenceWire.WritePose(bits, new PresencePoseRecord
                {
                    Millis = (uint)(Clock.Now / 1000),
                    Correction = Peer.Plausibility!.Correction,
                    Flags = flags,
                    Position = NetQuantization.QuantizePosition(new NetVector3(x, 0, 0)),
                    Rotation = NetQuantization.IdentityRotationBits,
                    EyeCentimeters = 160
                });
                Assert(avatars.SendDatagram(bits.AsSegment(), InternalSystemTarget.Host).Succeeded, "raw pose is sent");
                Raw.Flush();
                Frames(1);
            }
            internal void AssertAbsent(string message) => Assert(!Peer.HasPose && Position == null && Player.Entity == null
                && !Player.TryGetSnapshot(out _) && !Host.Runtime.Presence.TryGetAvatar(Raw.Id, out _), message);
            internal MotionMessage Grab(float x, int index = 0)
            {
                var body = MotionBox(Host, index);
                body.Position = new NetVector3(x, 0, 3);
                Assert(Host.World.Host.Index.TryFind(body.Entity, out var slot), "body has a scene key");
                var request = new MotionMessage { Target = new MotionTarget(slot.Key), Request = (uint)Clock.Now, PlayerPosition = new NetVector3(x, 0, 0) };
                var writer = new WorldWireWriter();
                MotionWire.Write(writer, MotionMessages.Grab, request);
                Assert(world.Send(writer.Segment, InternalSystemTarget.Host).Succeeded, "raw grab is sent");
                Raw.Flush(); Frames(3);
                var reader = new WorldWireReader();
                while (world.TryReceive(out var received))
                {
                    reader.Reset(received.Payload);
                    var reply = new MotionMessage();
                    if (reader.TryReadHeader(out var type) && type == MotionMessages.Verdict && MotionWire.Read(reader, type, reply) && reply.Request == request.Request)
                        return reply;
                }
                throw new InvalidOperationException("No motion verdict.");
            }
            public void Dispose() { services.Dispose(); Host.Runtime.UnloadAll(); }
        }
    }
}
