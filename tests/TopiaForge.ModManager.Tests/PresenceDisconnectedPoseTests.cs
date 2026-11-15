using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.PresenceAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class PresenceDisconnectedPoseTests
    {
        internal static void Run()
        {
            AQueuedPoseCannotRestoreADisconnectedParticipant();
            ADisconnectedPoseIsNeverRelayed();
            Console.WriteLine("PresenceDisconnectedPoseTests passed.");
        }

        private static void AQueuedPoseCannotRestoreADisconnectedParticipant()
        {
            var rig = new PresenceRig();
            rig.HostWith(1);
            var sender = rig.Players[0];
            var host = rig.Host;
            var peer = host.PeerOf(sender)!;
            That(peer.HasPose, "the connected participant starts with an accepted pose");
            var accepted = host.Counters.PosesAccepted;
            var forged = host.Counters.Forged;

            // Model the receive boundary after reconciliation reported link loss but before the channel drained
            // its last datagram. Keep the stream so the transport double cannot discard the packet for us.
            peer.IsConnected = false;
            peer.ClearPose();
            var bits = new NetBitWriter(InternalSystemLimits.MaximumDatagramBytes);
            PresenceWire.WritePose(bits, new PresencePoseRecord
            {
                Millis = (uint)(rig.Clock.Now / 1000),
                Position = NetQuantization.QuantizePosition(sender.Feet),
                Rotation = NetQuantization.IdentityRotationBits
            });
            var payload = bits.ToArray();
            host.Channel.InjectDatagram(sender.Id, payload);
            host.Presence.Host.Receive(rig.Clock.Now);
            That(!peer.HasPose && host.Counters.PosesAccepted == accepted && host.Counters.Forged == forged + 1,
                "a queued pose from a disconnected participant is refused before it can restore the cleared pose");
            That(!host.Presence.TryGetPose(sender.Id, out _), "the participant still has no current pose");

            peer.IsConnected = true;
            host.Channel.InjectDatagram(sender.Id, payload);
            host.Presence.Host.Receive(rig.Clock.Now);
            That(peer.HasPose && host.Counters.PosesAccepted == accepted + 1,
                "the same valid payload is accepted once the participant is connected again");
            foreach (var node in rig.All) node.Runtime.Disconnect();
        }

        private static void ADisconnectedPoseIsNeverRelayed()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            var sender = rig.Players[0];
            var observer = rig.Players[1];
            var host = rig.Host;
            var peer = host.PeerOf(sender)!;
            var log = new List<(ParticipantId To, byte[] Payload)>();
            host.Channel.DatagramLog = log;

            // Seed a residual pose independently of the receive guard. This proves the relay's own filter,
            // even if a future input path accidentally leaves a pose on a disconnected participant.
            peer.IsConnected = false;
            peer.SetPose(rig.Clock.Now, peer.Position, peer.Rotation, peer.Velocity, peer.EyeCentimeters, peer.Flags);
            host.PeerOf(observer)!.Scheduler!.Refresh(new NetworkEntityId(peer.Index));
            rig.Clock.Advance(PresenceHost.RelayIntervalMicros);
            host.Presence.Host.Send(rig.Clock.Now);
            That(!Contains(log, observer.Id, peer.Index), "no observer receives a disconnected participant's residual pose");

            peer.IsConnected = true;
            host.PeerOf(observer)!.Scheduler!.Refresh(new NetworkEntityId(peer.Index));
            log.Clear();
            rig.Clock.Advance(PresenceHost.RelayIntervalMicros);
            host.Presence.Host.Send(rig.Clock.Now);
            That(Contains(log, observer.Id, peer.Index), "the connected participant's same pose is relayed normally");
            foreach (var node in rig.All) node.Runtime.Disconnect();
        }

        private static bool Contains(List<(ParticipantId To, byte[] Payload)> log, ParticipantId recipient, byte index)
        {
            var reader = new NetBitReader();
            foreach (var item in log)
            {
                if (!item.To.Equals(recipient)) continue;
                That(PresenceWire.TryReadDatagramType(new ArraySegment<byte>(item.Payload), reader, out var type)
                    && type == PresenceMessages.Poses, "the relay datagram identifies its format");
                That(PresenceWire.TryReadPosesHeader(reader, out _, out var count), "the relay header is valid");
                for (var record = 0; record < count; record++)
                {
                    That(PresenceWire.TryReadRelay(reader, out var pose), "the relay record is valid");
                    if (pose.Index == index) return true;
                }
            }
            return false;
        }
    }
}
