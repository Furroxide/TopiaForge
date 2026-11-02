using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.PresenceAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class PresenceViewRootReconnectTests
    {
        internal static void Run()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            rig.Frames(40);
            var subject = rig.Players[0];
            subject.Presence.LocalTyping = true;
            rig.Frames(10);
            var viewers = new[] { rig.Host, rig.Players[1] };
            var tracked = viewers.Select(viewer =>
            {
                var peer = viewer.PeerOf(subject)!;
                var entity = viewer.AvatarOf(subject)!.Entity;
                That(viewer.Presence.TryCreatePlayerRef(entity, out var reference), "live root has a canonical reference before link loss");
                return (Viewer: viewer, Peer: peer, Entity: entity, Reference: reference, Lifetime: peer.PresentationLifetime);
            }).ToArray();
            subject.Paused = true;
            var accepted = rig.Host.Counters.PosesAccepted;
            var rules = rig.Host.PeerOf(subject)!.Plausibility!;
            var anchor = rules.Anchor;
            var strikes = rules.Strikes;
            rig.Network.LoseLink(subject.Control);
            foreach (var viewer in viewers) viewer.Runtime.BeforeModsUpdate(.016f);
            rig.Clock.Advance(16_000);
            foreach (var row in tracked)
            {
                That(!row.Peer.HasPose && !row.Viewer.Presence.TryGetPose(subject.Id, out _),
                    "disconnected subject retains c0 gameplay pose refusal");
                That(!row.Viewer.Presence.TryResolvePlayerRef(row.Reference, out _)
                    && !row.Viewer.Presence.IsCurrentViewAvatar(row.Entity, row.Viewer.World!),
                    "disconnected subject fails reference and native view admission");
                That(ReferenceEquals(row.Viewer.AvatarOf(subject)!.Entity, row.Entity), "disconnect retains the exact historical avatar root");
            }
            rig.Network.Restore(subject.Control);
            foreach (var viewer in viewers) viewer.Runtime.BeforeModsUpdate(.016f);
            rig.Clock.Advance(16_000);
            foreach (var row in tracked)
            {
                That(row.Peer.PresentationLifetime != row.Lifetime && !row.Peer.HasPose && row.Peer.Index == 0
                    && !row.Viewer.Presence.TryGetPose(subject.Id, out _)
                    && (row.Peer.Flags & (PresenceFlags.Talking | PresenceFlags.Typing)) == 0,
                    "new stream still invalidates gameplay lifetime pose index and activity");
                That(ReferenceEquals(row.Viewer.AvatarOf(subject)!.Entity, row.Entity)
                    && row.Viewer.Presence.TryResolvePlayerRef(row.Reference, out _)
                    && row.Viewer.Presence.IsCurrentViewAvatar(row.Entity, row.Viewer.World!),
                    "re-admitted same-identity exact view root recovers for host and client without a gameplay pose");
                That(!row.Viewer.Presence.IsCurrentViewAvatar(row.Entity, "unrelated-world"), "retained view root refuses an unrelated world");
            }
            That(rig.Host.Counters.PosesAccepted == accepted, "view recovery accepts no replacement pose");
            That(ReferenceEquals(rules, rig.Host.PeerOf(subject)!.Plausibility) && rules.Anchor.Equals(anchor) && rules.Strikes == strikes,
                "view-only recovery never resets anti-teleport validation");
            foreach (var row in tracked)
            {
                row.Viewer.SceneVersion++;
                That(!row.Viewer.Presence.IsCurrentViewAvatar(row.Entity, row.Viewer.World!), "retained view root refuses a replaced scene instance");
            }
            foreach (var node in rig.All) node.Runtime.Disconnect();
            Console.WriteLine("Presence view-root reconnect passed: live root recovery with strict gameplay lifetime invalidation.");
        }
    }
}
