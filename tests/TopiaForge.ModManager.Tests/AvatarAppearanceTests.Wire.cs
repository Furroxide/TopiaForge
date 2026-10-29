using System;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class AvatarAppearanceTests
    {
        private static void WireAndPrivacy()
        {
            var rig = new PresenceRig();
            using var host = Bind(rig.Host);
            rig.HostWith(1); rig.Frames(20);
            var peer = rig.Host.PeerOf(rig.Players[0])!;
            var target = host.Player(peer.Id);
            using var lease = Lease(target.Appearance.Set(Red));
            using var label = Lease(target.Appearance.SetNameplate("Sam"));
            foreach (var text in new[] { "Sam\nOther", new string('a', 33) })
                Check(target.Appearance.SetNameplate(text).ErrorCode == ModErrorCode.InvalidArgument, "unsafe nameplate is rejected");
            using (var redacted = Lease(target.Appearance.SetNameplate("127.0.0.1 [::1]")))
                Check(peer.Presentation!.Name!.Contains("[address]", StringComparison.Ordinal) && !peer.Presentation.Name.Contains("127.0.0.1", StringComparison.Ordinal) && !peer.Presentation.Name.Contains("::1", StringComparison.Ordinal), "addresses are redacted before publication");
            using (var punctuation = Lease(target.Appearance.SetNameplate("Sam: wait...")))
                Check(peer.Presentation!.Name == "Sam: wait...", "meaningful punctuation survives");
            var writer = new WorldWireWriter(PresencePresentationWire.MaximumBytes);
            PresencePresentationWire.Write(writer, rig.Host.Control.SessionSnapshot.Id.Value, peer);
            var bytes = writer.Segment.ToArray();
            var reader = new WorldWireReader();
            Check(PresenceWire.TryReadMessageType(reader, bytes, out var type) && type == PresencePresentationWire.Message
                && PresencePresentationWire.TryRead(reader, out var read) && read!.Appearance.Equals(Red) && read.Name == "Sam", "wire round-trip preserves full effective state");
            for (var i = 0; i < bytes.Length; i++)
            {
                var cut = new byte[i]; Array.Copy(bytes, cut, i);
                var accepted = PresenceWire.TryReadMessageType(reader, cut, out _) && PresencePresentationWire.TryRead(reader, out _);
                Check(!accepted, "every truncation is atomic");
            }
            var trailing = new byte[bytes.Length + 1]; Array.Copy(bytes, trailing, bytes.Length);
            PresenceWire.TryReadMessageType(reader, trailing, out _);
            Check(!PresencePresentationWire.TryRead(reader, out _), "trailing bytes rejected");
            bytes[bytes.Length - 1] = 2;
            PresenceWire.TryReadMessageType(reader, bytes, out _);
            Check(!PresencePresentationWire.TryRead(reader, out _), "undefined boolean rejected");
            Check(State(rig.Host, rig.Players[0]).Current.Equals(Red), "malformed wire never changes canonical state");
        }
    }
}
