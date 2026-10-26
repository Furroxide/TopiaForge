using System;
using System.IO;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestPresenceGatesMotionAcrossRuntimes(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new WorldGame(NewFixture(Path.Combine(root, "presence-motion"), "presence", PresenceModEntry), network.Host, clock);
            try
            {
                host.Game.Presence.HasPlayer = true;
                host.Start();
                host.LoadScene("Yard", true);
                var near = MotionBox(host, 0);
                var far = MotionBox(host, 1);
                far.Position = new NetVector3(900f, 0f, 3f);
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Presence motion", 4)).Succeeded, "the host opens");
                WorldFrames(new[] { host }, clock, 3);
                var raw = network.AddPlayer("raw");
                var avatars = raw.OpenSystemChannel(InternalSystemChannelKind.Avatars).Value!;
                var world = raw.OpenSystemChannel(InternalSystemChannelKind.WorldReplication).Value!;
                network.Join(raw);
                Frames(3);

                Assert(host.Runtime.Presence.ValidatedPosition(raw.Id) == null, "a player without a pose has no validated position");
                var missing = Grab(near, NetVector3.Zero);
                Assert(missing.State == OwnershipState.Server && missing.Result == OwnershipResult.NoChange && !near.Kinematic,
                    "a grab before the first validated pose fails closed even when its packet reports a nearby player");

                Pose(NetVector3.Zero);
                var anchor = host.Runtime.Presence.ValidatedPosition(raw.Id);
                Assert(anchor.HasValue && NearPoint(anchor.Value, NetVector3.Zero), "the host accepts the player's initial pose");
                Pose(new NetVector3(900f, 0f, 0f));
                Assert(host.Runtime.Presence.Counters.Corrections == 1 && host.Runtime.Presence.ValidatedPosition(raw.Id).Equals(anchor),
                    "an unflagged pose 900 metres away is corrected without moving the validated anchor");
                var forged = Grab(far, new NetVector3(900f, 0f, 0f));
                Assert(forged.Result == OwnershipResult.OutOfRange && forged.State == OwnershipState.Server && !far.Kinematic,
                    "a motion grab relying on that forged position is refused specifically as out of range");
                var honest = Grab(near, NetVector3.Zero);
                Assert(honest.Result == OwnershipResult.Granted && honest.State == OwnershipState.Owned && near.Kinematic,
                    "an honest grab within range of the validated anchor still succeeds");

                Frames(160);
                Assert(host.Runtime.Presence.ValidatedPosition(raw.Id) == null, "a silent player's validated position expires");
                var stale = Grab(MotionBox(host, 2), NetVector3.Zero);
                Assert(stale.State == OwnershipState.Server && stale.Result == OwnershipResult.NoChange,
                    "a stale validated position never falls back to the grab packet");
                Assert(host.World.Motion.GrabsGranted == 1 && host.World.Motion.GrabsRefused == 3 && host.Fixture.Logger.Errors.Count == 0,
                    "exactly one honest grab succeeds and all three invalid grabs are refused without a runtime error");
                Console.WriteLine("Presence motion integration passed: missing, forged and stale positions refused; honest grab granted.");

                void Frames(int count)
                {
                    for (var frame = 0; frame < count; frame++)
                    {
                        raw.Pump();
                        host.Frame();
                        raw.Flush();
                        clock.Now += 16_000;
                    }
                }

                void Pose(NetVector3 feet)
                {
                    var bits = new NetBitWriter(PresenceMessages.PoseBytes);
                    PresenceWire.WritePose(bits, new PresencePoseRecord
                    {
                        Millis = (uint)(clock.Now / 1000),
                        Position = NetQuantization.QuantizePosition(feet),
                        Rotation = NetQuantization.IdentityRotationBits,
                        EyeCentimeters = 160
                    });
                    Assert(avatars.SendDatagram(bits.AsSegment(), InternalSystemTarget.Host).Succeeded, "the raw pose is sent");
                    raw.Flush();
                    Frames(3);
                }

                MotionMessage Grab(FakeWorldObject body, NetVector3 reportedPosition)
                {
                    Assert(host.World.Host.Index.TryFind(body.Entity, out var slot), "the requested body has a scene key");
                    var message = new MotionMessage { Target = new MotionTarget(slot.Key), Request = (uint)clock.Now, PlayerPosition = reportedPosition };
                    var writer = new WorldWireWriter();
                    MotionWire.Write(writer, MotionMessages.Grab, message);
                    Assert(world.Send(writer.Segment, InternalSystemTarget.Host).Succeeded, "the raw grab is sent");
                    raw.Flush();
                    Frames(3);
                    var reader = new WorldWireReader();
                    var reply = new MotionMessage();
                    while (world.TryReceive(out var received))
                    {
                        reader.Reset(received.Payload);
                        if (reader.TryReadHeader(out var type) && type == MotionMessages.Verdict && MotionWire.Read(reader, type, reply) && reply.Request == message.Request)
                            return reply;
                    }

                    throw new InvalidOperationException("The host did not answer the raw grab.");
                }
            }
            finally
            {
                host.Runtime.UnloadAll();
            }
        }
    }
}
