using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.PresenceAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Hostile games against presence (WS3.10). A player that writes its own payloads cannot claim flags the host can
    /// check, health no game reports, another player's pose, or the host's voice; a bent payload changes nothing and ends
    /// nothing. A hostile host cannot make a player move itself through a relayed pose or take half a datagram.
    /// </summary>
    internal static class PresenceHostileTests
    {
        internal static void Run()
        {
            ForgedFlagsAreRefused();
            AnotherPlayersPoseIsRefused();
            TheHostsMessagesFromAPlayerAreRefused();
            ForgedHealthIsRefused();
            BentPayloadsChangeNothing();
            AHostileHostCannotBendAPlayer();
            Console.WriteLine("PresenceHostileTests passed.");
        }

        private static PresencePeer PeerOf(PresenceNode viewer, RawPresencePlayer raw) =>
            viewer.Presence.TryGetPeer(raw.Id, out var peer) && peer != null ? peer : throw new InvalidOperationException("Presence test failed: the host knows " + raw.Id);

        private static void ForgedFlagsAreRefused()
        {
            var rig = new PresenceRig();
            rig.HostWith(1);
            var host = rig.Host;
            var rogue = rig.AddRawPlayer("rogue");
            rig.Frames(6);
            rogue.SendPose(new NetVector3(1f, 0f, 1f));
            rig.Frames(2);
            var peer = PeerOf(host, rogue);
            That(peer.HasPose && host.Counters.PosesAccepted > 0, "a raw player's honest pose stands");

            // Away while moving is cleared; away while still stands.
            rogue.SendPose(new NetVector3(3f, 0f, 1f), PresenceFlags.Away);
            rig.Frames(2);
            That((peer.Flags & PresenceFlags.Away) == 0 && host.Counters.FlagsRefused == 1, "a player that moved is not away, whatever it says");
            rogue.SendPose(new NetVector3(3f, 0f, 1f), PresenceFlags.Away);
            rig.Frames(2);
            That((peer.Flags & PresenceFlags.Away) != 0, "standing still, it may say so");

            // Talking is the host's voice relay's to say.
            rogue.SendPose(new NetVector3(3f, 0f, 1f), PresenceFlags.Talking);
            rig.Frames(2);
            That((peer.Flags & PresenceFlags.Talking) == 0 && host.Counters.FlagsRefused == 2, "a player the host's relay never heard is not talking");

            // Teleports: two stand at once, the third within ten seconds is corrected back to the last landing.
            rogue.SendPose(new NetVector3(500f, 0f, 1f), PresenceFlags.Teleported);
            rig.Frames(2);
            rogue.SendPose(new NetVector3(-500f, 0f, 1f), PresenceFlags.Teleported);
            rig.Frames(2);
            That(host.Counters.Corrections == 0 && Near(host.Presence.ValidatedPosition(rogue.Id)!.Value, new NetVector3(-500f, 0f, 1f), 0.01f), "two flagged teleports stand");
            That((peer.Flags & PresenceFlags.Teleported) == 0, "and the flag is never relayed");
            rogue.SendPose(new NetVector3(500f, 0f, 1f), PresenceFlags.Teleported);
            rig.Frames(3);
            var corrections = rogue.Corrections();
            That(host.Counters.Corrections == 1 && corrections.Count == 1 && corrections[0].Number == 1 && Near(corrections[0].Target, new NetVector3(-500f, 0f, 1f), 0.01f),
                "a third is corrected back to the last landing, under correction 1");

            // Poses on their way when the correction left cost nothing for a second; after that the old number is a strike.
            rogue.SendPose(new NetVector3(-499f, 0f, 1f));
            rig.Frames(2);
            That(host.Counters.PosesIgnored == 1 && host.Counters.Corrections == 1, "a pose still on the old number is ignored for a moment");
            rogue.SendPose(new NetVector3(-499f, 0f, 1f), correction: 1);
            rig.Frames(2);
            That(Near(host.Presence.ValidatedPosition(rogue.Id)!.Value, new NetVector3(-499f, 0f, 1f), 0.01f), "a pose on the new number stands");
            rig.Frames(70);
            rogue.SendPose(new NetVector3(-499f, 0f, 1f));
            rig.Frames(3);
            That(host.Counters.Corrections == 2 && rogue.Corrections().Last().Number == 2, "a player still on the old number a second later never took the correction, and is struck");

            // A flag no version knows makes the pose malformed: dropped whole, nothing else.
            var bits = new NetBitWriter(InternalSystemLimits.MaximumDatagramBytes);
            PresenceWire.WritePose(bits, new PresencePoseRecord { Millis = (uint)(rig.Clock.Now / 1000), Correction = 2, Position = NetQuantization.QuantizePosition(new NetVector3(-498f, 0f, 1f)) });
            var unknown = bits.ToArray();
            unknown[7] |= 0x40;
            var malformed = host.Counters.Malformed;
            rogue.SendDatagram(unknown);
            rig.Frames(2);
            That(host.Counters.Malformed == malformed + 1 && Near(host.Presence.ValidatedPosition(rogue.Id)!.Value, new NetVector3(-499f, 0f, 1f), 0.01f),
                "an unknown flag makes the pose malformed, and nothing moves");
            That(rig.Network.Kicks.Count == 0 && rig.Network.Members.Count == 2, "and the rogue is still in the session: two strikes are not five");
        }

        private static void AnotherPlayersPoseIsRefused()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            var host = rig.Host;
            var victim = rig.Players[0];
            var watcher = rig.Players[1];
            var rogue = rig.AddRawPlayer("rogue");
            rig.Frames(8);
            var index = host.PeerOf(victim)!.Index;
            var before = host.PeerOf(victim)!.Position;
            var validated = host.Presence.ValidatedPosition(victim.Id);

            // The victim keeps still and sends nothing meanwhile, so nothing of its own could hide a forgery.
            victim.Paused = true;
            var forgery = new PresencePoseRecord { Index = index, Position = NetQuantization.QuantizePosition(new NetVector3(800f, 0f, 800f)), Rotation = NetQuantization.IdentityRotationBits };
            var forged = host.Counters.Forged;
            That(rogue.SendRelay(forgery).Succeeded, "the rogue sends a pose in the host's format, naming the victim");
            rig.Frames(2);
            That(rogue.SendRelay(new PresencePoseRecord { Index = PeerOf(host, rogue).Index, Position = NetQuantization.QuantizePosition(new NetVector3(4f, 0f, 4f)), Rotation = NetQuantization.IdentityRotationBits }).Succeeded,
                "and one naming itself");
            rig.Frames(8);
            That(host.Counters.Forged == forged + 2, "the host refuses both: only the host speaks for players by index (" + (host.Counters.Forged - forged) + ")");
            That(host.PeerOf(victim)!.Position.Equals(before) && host.Presence.ValidatedPosition(victim.Id).Equals(validated), "the victim's pose on the host is untouched");
            That(host.Presence.TryGetPose(victim.Id, out var pose) && Math.Abs(pose.Position.X - victim.Feet.X) < 0.01f, "and the session's lookup still has it where it stands");
            That(Near(watcher.AvatarOf(victim)!.Feet, OnGrid(victim.Feet), 0.01f), "every other player still sees it there");
            That(!PeerOf(host, rogue).HasPose, "and the rogue has no pose from it either");
            victim.Paused = false;
        }

        private static void TheHostsMessagesFromAPlayerAreRefused()
        {
            var rig = new PresenceRig();
            rig.HostWith(1);
            var host = rig.Host;
            var victim = rig.Players[0];
            var rogue = rig.AddRawPlayer("rogue");
            rig.Frames(8);
            var index = host.PeerOf(victim)!.Index;
            var rosterVersion = host.Presence.Roster.Version;
            var teleports = victim.Backend.Teleports.Count;
            var forged = host.Counters.Forged;

            rogue.SendMessage(writer => PresenceWire.WriteRoster(writer, new List<KeyValuePair<byte, ParticipantId>> { new KeyValuePair<byte, ParticipantId>(1, rogue.Id) }));
            rogue.SendMessage(writer => PresenceWire.WriteStates(writer, new List<PresenceStateRecord> { new PresenceStateRecord(index, 1f, 100f, "item.bomb") }));
            rogue.SendMessage(writer => PresenceWire.WritePlace(writer, "world-one", 0, new NetVector3(5f, 0f, 5f), 0f));
            rogue.SendMessage(writer => PresenceWire.WriteCorrect(writer, 9, new NetVector3(5f, 0f, 5f)));
            rig.Frames(4);
            That(host.Counters.Forged == forged + 4, "the host refuses each of its own messages from a player (" + (host.Counters.Forged - forged) + ")");
            That(host.Presence.Roster.Version == rosterVersion && host.Presence.Roster.TryGetIndex(rogue.Id, out var rogueIndex) && rogueIndex != 1,
                "the roster is the host's alone");
            That(host.PeerOf(victim)!.HeldItem != "item.bomb" && victim.Backend.Teleports.Count == teleports && host.Backend.Teleports.Count == 0,
                "nobody's state changes and nobody moves");
        }

        private static void ForgedHealthIsRefused()
        {
            var rig = new PresenceRig();
            rig.HostWith(1);
            var host = rig.Host;
            var watcher = rig.Players[0];
            var rogue = rig.AddRawPlayer("rogue");
            rig.Frames(8);
            rogue.SendPose(new NetVector3(2f, 0f, 2f));
            var peer = PeerOf(host, rogue);

            rogue.SendMessage(writer => PresenceWire.WriteState(writer, 150f, 100f, null));
            rig.Frames(2);
            That(host.Counters.StatesRefused == 1 && peer.Health == 0f, "health past the maximum is refused whole");
            rogue.SendMessage(writer => PresenceWire.WriteState(writer, 80f, 100f, "item.wrench"));
            rig.Frames(2);
            That(peer.Health == 80f && peer.MaximumHealth == 100f && peer.HeldItem == "item.wrench", "an honest report stands");
            rogue.SendMessage(writer => PresenceWire.WriteState(writer, 100f, 100f, "item.wrench"));
            rig.Frames(2);
            That(host.Counters.StatesRefused == 2 && peer.Health < 82f, "full health at once is held to regeneration (" + peer.Health + ")");
            rogue.SendMessage(writer => PresenceWire.WriteState(writer, 50f, 5000f, "item.wrench"));
            rig.Frames(2);
            That(host.Counters.StatesRefused == 3 && peer.MaximumHealth == 100f && peer.Health == 50f, "a maximum that changes is refused; health is read against the first");
            var refused = host.Counters.StatesRefused;
            rogue.SendMessage(writer => PresenceWire.WriteState(writer, float.NaN, 100f, null));
            rogue.SendMessage(writer => PresenceWire.WriteState(writer, -5f, 100f, null));
            rogue.SendMessage(writer => PresenceWire.WriteState(writer, 10f, 2_000_000f, null));
            rig.Frames(3);
            That(host.Counters.StatesRefused == refused + 3 && peer.Health == 50f && peer.MaximumHealth == 100f, "values no game reports are refused, and change nothing");
            var malformed = host.Counters.Malformed;
            rogue.SendMessage(writer => PresenceWire.WriteState(writer, 40f, 100f, "item\u0000bomb"));
            rig.Frames(2);
            That(host.Counters.Malformed == malformed + 1 && peer.Health == 50f, "a held item with a control character makes the state malformed");
            rig.Frames(10);
            That(watcher.Presence.TryGetPose(rogue.Id, out var seen) && seen.Health == 50f && seen.MaximumHealth == 100f, "every player sees what the host kept");
        }

        private static void BentPayloadsChangeNothing()
        {
            var rig = new PresenceRig();
            rig.HostWith(1);
            var host = rig.Host;
            var rogue = rig.AddRawPlayer("rogue");
            rig.Frames(8);
            rogue.SendPose(new NetVector3(2f, 0f, 2f));
            rig.Frames(2);
            var bits = new NetBitWriter(InternalSystemLimits.MaximumDatagramBytes);
            PresenceWire.WritePose(bits, new PresencePoseRecord { Millis = (uint)(rig.Clock.Now / 1000), Position = NetQuantization.QuantizePosition(new NetVector3(3f, 0f, 3f)) });
            var pose = bits.ToArray();
            var malformed = host.Counters.Malformed;

            rogue.SendDatagram(new byte[] { 1 });
            rogue.SendDatagram(new byte[] { 2, 1, 0, 0 });
            rogue.SendDatagram(pose.Take(pose.Length - 1).ToArray());
            rogue.SendDatagram(pose.Concat(new byte[] { 0 }).ToArray());
            rig.Frames(2);
            rogue.SendDatagram(new byte[] { 1, 9, 0, 0 });
            rogue.SendMessage(new byte[] { 1 });
            rogue.SendMessage(new byte[] { 1, 99 });
            rogue.SendMessage(writer => PresenceWire.WriteArrived(writer, "has space"));
            rig.Frames(2);
            That(host.Counters.Malformed == malformed + 8, "every bent payload is counted malformed (" + (host.Counters.Malformed - malformed) + ")");
            That(Near(host.Presence.ValidatedPosition(rogue.Id)!.Value, new NetVector3(2f, 0f, 2f), 0.01f), "and moves nothing");
            That(rig.Network.Kicks.Count == 0 && rig.Network.IsConnected(rogue.Control) && host.Log.Lines.All(line => !line.StartsWith("error", StringComparison.Ordinal)),
                "a bent payload ends nothing and logs no error");
        }

        /// <summary>A host with no runtime behind it, which writes presence's payloads itself, and one real player.</summary>
        private static void AHostileHostCannotBendAPlayer()
        {
            var network = new LoopbackNetPair();
            var clock = new ManualClock();
            var hostControl = network.Host;
            var channel = (LoopbackChannel)hostControl.OpenSystemChannel(InternalSystemChannelKind.Avatars).Value!;
            var player = new PresenceNode(network.AddPlayer("p1"), clock, "p1");
            player.Runtime.Connect();
            network.StartHosting();
            network.Join(player.Control);
            void Frames(int count)
            {
                for (var frame = 0; frame < count; frame++)
                {
                    hostControl.Pump();
                    while (channel.TryReceive(out _))
                    {
                    }

                    while (channel.TryReceiveDatagram(out _))
                    {
                    }

                    player.Frame();
                    hostControl.Flush();
                    clock.Advance(16_000);
                }
            }

            var writer = new WorldWireWriter(PresenceMessages.MaximumMessageBytes);
            var bits = new NetBitWriter(InternalSystemLimits.MaximumDatagramBytes);
            void Message(Action<WorldWireWriter> write)
            {
                write(writer);
                channel.Send(writer.Segment, InternalSystemTarget.Everyone);
            }

            PresencePoseRecord Record(byte index, float x) => new PresencePoseRecord
            {
                Index = index,
                Position = NetQuantization.QuantizePosition(new NetVector3(x, 0f, 0f)),
                Rotation = NetQuantization.IdentityRotationBits
            };

            byte[] Build(params PresencePoseRecord[] records)
            {
                PresenceWire.BeginPoses(bits, (uint)(clock.Now / 1000), records.Length);
                foreach (var record in records) PresenceWire.WriteRelay(bits, record);
                return bits.ToArray();
            }

            void Relay(params PresencePoseRecord[] records) => channel.SendDatagram(new ArraySegment<byte>(Build(records)), InternalSystemTarget.Everyone);

            Frames(3);
            That(player.Presence.Realm == NetRealm.Client, "the player joins the raw host");
            Message(w => PresenceWire.WriteRoster(w, new List<KeyValuePair<byte, ParticipantId>>
            {
                new KeyValuePair<byte, ParticipantId>(1, hostControl.Id),
                new KeyValuePair<byte, ParticipantId>(2, player.Id)
            }));
            Frames(2);
            That(player.PeerOf(player)!.Index == 2, "the player takes its index from the host's roster");
            var hostPeer = player.Presence.Peers.First(peer => peer.Id.Equals(hostControl.Id));
            Relay(Record(1, 10f));
            Frames(2);
            That(hostPeer.HasPose && Math.Abs(NetQuantization.DequantizePosition(hostPeer.Position).X - 10f) < 0.01f, "a relayed pose for the host stands");

            // A host's states are untrusted too: no state may poison the public pose lookup, and a bad record refuses
            // the whole message, including valid records before it.
            Message(w => PresenceWire.WriteStates(w, new List<PresenceStateRecord> { new PresenceStateRecord(1, 80f, 100f, "wrench") }));
            Frames(2);
            foreach (var invalid in new (float Health, float Maximum)[]
            {
                (float.NaN, 100f), (50f, float.NaN), (float.PositiveInfinity, 100f), (50f, float.PositiveInfinity),
                (-1f, 100f), (101f, 100f), (0f, -1f), (1f, 0f)
            })
            {
                var refused = player.Counters.Malformed;
                Message(w => PresenceWire.WriteStates(w, new List<PresenceStateRecord>
                {
                    new PresenceStateRecord(1, 20f, 100f, null),
                    new PresenceStateRecord(1, invalid.Health, invalid.Maximum, null)
                }));
                Frames(2);
                That(player.Presence.TryGetPose(hostControl.Id, out var safe) && safe.Health == 80f && safe.MaximumHealth == 100f && safe.HeldItemId == "wrench",
                    "an invalid host health report preserves the last usable public pose");
                That(player.Counters.Malformed == refused + 1, "an invalid host health report is refused whole and counted");
            }

            // The normal host uses zeroes when health is unavailable. That remains a valid state, and recovery works.
            Message(w => PresenceWire.WriteStates(w, new List<PresenceStateRecord> { new PresenceStateRecord(1, 0f, 0f, null) }));
            Frames(2);
            That(player.Presence.TryGetPose(hostControl.Id, out var unknownHealth) && unknownHealth.Health == 0f && unknownHealth.MaximumHealth == 0f,
                "a host without health data still provides a usable pose");
            Message(w => PresenceWire.WriteStates(w, new List<PresenceStateRecord> { new PresenceStateRecord(1, 90f, 100f, null) }));
            Frames(2);
            That(player.Presence.TryGetPose(hostControl.Id, out var recovered) && recovered.Health == 90f,
                "a valid state after a refused host report is still taken");

            // Half a datagram is no datagram: a relay that promises two poses and carries one and a half changes nothing, nor
            // does one with a bit to spare. The whole relay goes first and stands, and the bent ones after it change nothing more.
            var malformed = player.Counters.Malformed;
            Relay(Record(1, 11f));
            Frames(2);
            var afterWhole = hostPeer.Position;
            That(Math.Abs(NetQuantization.DequantizePosition(afterWhole).X - 11f) < 0.01f, "a whole relay stands");
            var two = Build(Record(1, 13f), Record(1, 14f));
            channel.SendDatagram(new ArraySegment<byte>(two, 0, two.Length - 3), InternalSystemTarget.Everyone);
            var spare = Build(Record(1, 15f));
            spare[spare.Length - 1] |= 0x80;
            channel.SendDatagram(new ArraySegment<byte>(spare), InternalSystemTarget.Everyone);
            Frames(2);
            That(player.Counters.Malformed == malformed + 2 && hostPeer.Position.Equals(afterWhole), "a cut or padded relay is refused whole");

            // A relay naming the player itself never moves it, and one naming nobody is skipped.
            var teleports = player.Backend.Teleports.Count;
            Relay(Record(2, 900f), Record(9, 900f));
            Frames(2);
            That(player.Backend.Teleports.Count == teleports && Math.Abs(player.Feet.X) < 0.01f && player.Presence.Peers.All(peer => !peer.HasPose || NetQuantization.DequantizePosition(peer.Position).X < 100f),
                "a relayed pose never moves the player itself, nor names a player the roster does not");

            // A host sending in a player's format speaks for nobody.
            var forged = player.Counters.Forged;
            PresenceWire.WritePose(bits, new PresencePoseRecord { Millis = (uint)(clock.Now / 1000), Position = NetQuantization.QuantizePosition(new NetVector3(50f, 0f, 0f)) });
            channel.SendDatagram(bits.AsSegment(), InternalSystemTarget.Everyone);
            Message(w => PresenceWire.WriteState(w, 1f, 100f, null));
            Message(w => PresenceWire.WriteArrived(w, "world-one"));
            Frames(2);
            That(player.Counters.Forged == forged + 3, "a player's own pose, state or arrival from the host is refused (" + (player.Counters.Forged - forged) + ")");

            // A placement in another world is not this one's; a correction is the host's to make.
            Message(w => PresenceWire.WritePlace(w, "world-two", 3, new NetVector3(7f, 0f, 7f), 90f));
            Frames(2);
            That(player.Backend.Teleports.Count == teleports, "a placement in a world the player does not run moves nothing");
            Message(w => PresenceWire.WritePlace(w, "world-one", 3, new NetVector3(7f, 0f, 7f), 90f));
            Frames(2);
            That(player.Backend.Teleports.Count == teleports + 1 && Near(player.Feet, new NetVector3(7f, 0f, 7f), 0.001f) && !player.Backend.Teleports.Last().KeepFacing,
                "one in its own world moves it there, facing the way the host says");
            Message(w => PresenceWire.WriteCorrect(w, 5, new NetVector3(6f, 0f, 6f)));
            Frames(2);
            That(player.Presence.Client.Correction == 5 && Near(player.Feet, new NetVector3(6f, 0f, 6f), 0.001f) && player.Backend.Teleports.Last().KeepFacing,
                "a correction moves it back, keeping its facing, and its poses carry the new number");

            // A roster that names index 0 or a participant twice is refused whole, and the one held stays.
            var version = player.Presence.Roster.Version;
            Message(w =>
            {
                PresenceWire.BeginMessage(w, PresenceMessages.Roster);
                w.WriteVarUInt(1);
                w.WriteByte(0);
                w.WriteString(hostControl.Id.Value);
            });
            Frames(2);
            That(player.Presence.Roster.Version == version && player.PeerOf(player)!.Index == 2, "a bent roster is refused, and the player keeps the host's last one");
            That(player.Log.Lines.All(line => !line.StartsWith("error", StringComparison.Ordinal)), "and nothing logged an error");
        }
    }
}
