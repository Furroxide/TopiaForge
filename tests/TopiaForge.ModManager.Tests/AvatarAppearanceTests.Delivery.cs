using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class AvatarAppearanceTests
    {
        private static void BodyAndBackpressure()
        {
            var rig = new PresenceRig();
            using var host = Bind(rig.Host);
            using var bodiesLifetime = new OwnerModLifetime();
            var factory = new FakeBodyFactory();
            var seam = rig.Host.Replication.Bind("io.example.appearance", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), bodiesLifetime);
            Check(seam.RegisterAvatarBodyFactory(factory).Succeeded, "existing cosmetic provider composes");
            rig.HostWith(1); rig.Frames(20);
            var node = rig.Players[0];
            var target = host.Player(node.Id);
            Check(target.Appearance.TryGet(out var original), "baseline available");
            var initialBody = rig.Host.AvatarOf(node)!.Body!;
            using var sourceLook = Lease(host.Player(rig.Host.Id).Appearance.Set(Red));
            var copy = Lease(target.Appearance.CopyFrom(host.Player(rig.Host.Id)));
            var pose = Lease(target.Appearance.SetPoweredDown());
            rig.Frames(20);
            var request = factory.Made[factory.Made.Count - 1].Request;
            Check(request.Appearance.Equals(Red) && request.PoweredDown && !initialBody.IsAlive, "provider receives plastic, metal, accessory and pose and releases old body");
            Check(State(node, node).Current.Equals(Red) && State(node, node).PoweredDown, "copy is automatically replicated");
            rig.Network.HostWindowMessages = 8;
            node.Paused = true;
            var writer = new WorldWireWriter(16);
            PresenceWire.WriteStates(writer, Array.Empty<PresenceStateRecord>());
            var destination = InternalSystemTarget.Only(node.Id);
            var fill = 0;
            while (fill < 256 && rig.Host.Channel.CanSend(destination, writer.Length))
            {
                Check(rig.Host.Channel.Send(writer.Segment, destination).Succeeded, "bounded test window fills");
                fill++;
            }
            Check(fill < 256 && !rig.Host.Channel.CanSend(destination, writer.Length), "recipient window is actually full");
            copy.Dispose(); pose.Dispose();
            var subject = rig.Host.PeerOf(node)!;
            rig.Frames(20);
            Check(subject.PresentationSent[subject.Index] != subject.PresentationVersion, "blocked send is not acknowledged");
            node.Paused = false; rig.Frames(40);
            Check(State(node, node).Current.Equals(original) && !State(node, node).PoweredDown, "restoration retries after window opens");
            Check(factory.Made[factory.Made.Count - 1].Request.Appearance.Equals(original)
                && !factory.Made[factory.Made.Count - 1].Request.PoweredDown, "restoration recreates original cosmetic body");
            var messages = rig.Host.Channel.SentMessages;
            rig.Frames(30);
            Check(rig.Host.Channel.SentMessages == messages, "settled appearance does not keep publishing");
            using var held = Lease(target.Appearance.Set(Blue));
            rig.Frames(15);
            rig.Network.LoseLink(node.Control); rig.Frames(3);
            rig.Network.Restore(node.Control); rig.Frames(35);
            Check(held.IsActive && target.IsPresent && State(node, node).Current.Equals(Blue), "active lease survives retained-player reconnect");
            rig.Network.Leave(node.Control); rig.Frames(3);
            Check(!held.IsActive && !target.IsPresent, "departure releases lease permanently");
            rig.Join(node); rig.Frames(25);
            Check(!State(node, node).Current.Equals(Blue), "new admission never inherits departed presentation");
        }

        private static void BoundedPublication()
        {
            var rig = new PresenceRig();
            using var host = Bind(rig.Host);
            rig.HostWith(7); rig.Frames(60);
            foreach (var player in host.Directory.Players)
            {
                Lease(player.Appearance.Set(Red));
                Lease(player.Appearance.Set(Blue));
            }
            var log = new List<(ParticipantId To, byte[] Payload)>();
            rig.Host.Channel.Log = log;
            rig.Clock.Advance(100_000);
            rig.Host.Presence.SendPresentations(rig.Host.Channel);
            var counts = new Dictionary<ParticipantId, int>();
            var reader = new WorldWireReader();
            foreach (var message in log)
            {
                Check(message.Payload.Length <= PresencePresentationWire.MaximumBytes, "record payload is bounded");
                Check(PresenceWire.TryReadMessageType(reader, message.Payload, out var kind) && kind == PresencePresentationWire.Message
                    && PresencePresentationWire.TryRead(reader, out var state) && state!.Appearance.Equals(Blue), "superseded appearance is coalesced before publication");
                counts[message.To] = counts.TryGetValue(message.To, out var count) ? count + 1 : 1;
            }
            Check(counts.Count == 7, "every recipient is serviced independently");
            foreach (var count in counts.Values) Check(count == 4, "at most four records per recipient per beat");
            var total = log.Count;
            rig.Host.Presence.SendPresentations(rig.Host.Channel);
            Check(log.Count == total, "repeated calls within a beat send nothing");
            rig.Frames(40);
            foreach (var viewer in rig.Players)
                foreach (var subject in rig.Players)
                    Check(State(viewer, subject).Current.Equals(Blue), "bounded baseline eventually reaches every recipient");
        }
    }
}
