using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Worlds;
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
            using var visibility = new OwnedRendererVisibility<Renderer>(_ => true, r => r.Enabled, (r, enabled) => r.Enabled = enabled);
            var tracked = viewers.Select(viewer =>
            {
                var peer = viewer.PeerOf(subject)!;
                var avatar = viewer.AvatarOf(subject)!;
                var entity = avatar.Entity;
                That(viewer.Presence.TryCreatePlayerRef(entity, out var reference), "live root has a canonical reference before link loss");
                That(avatar.IsVisible && HasNametag(viewer.Presence), viewer.Name + " shows the avatar and nametag before link loss");
                var renderer = new Renderer();
                var parts = new[] { renderer };
                // Mirror the native renderer's dynamic hold, including its current-avatar admission predicate.
                visibility.Hide(() => parts, () => viewer.Presence.IsCurrentViewAvatar(entity, viewer.World!));
                That(!renderer.Enabled, viewer.Name + " starts with an admitted renderer hold");
                return (Viewer: viewer, Peer: peer, Avatar: avatar, Entity: entity, Reference: reference,
                    Lifetime: peer.PresentationLifetime, PoseMicros: peer.PoseMicros, Renderer: renderer);
            }).ToArray();

            void AssertSuppressed(string stage)
            {
                foreach (var row in tracked)
                    That(!row.Peer.HasPose && row.Peer.PoseMicros == row.PoseMicros
                        && !row.Avatar.IsVisible && !HasNametag(row.Viewer.Presence),
                        row.Viewer.Name + " suppresses the retained avatar and nametag " + stage);
            }

            bool HasNametag(MultiplayerPresence presence)
            {
                for (var slot = 0; slot < presence.NametagCount; slot++)
                    if (presence.TryGetNametag(slot, out var tag) && tag.DisplayName == subject.Control.DisplayName) return true;
                return false;
            }

            subject.Paused = true;
            var rules = rig.Host.PeerOf(subject)!.Plausibility!;
            var anchor = rules.Anchor;
            var strikes = rules.Strikes;
            rig.Network.LoseLink(subject.Control);
            foreach (var viewer in viewers) viewer.Runtime.BeforeModsUpdate(.016f);
            rig.Clock.Advance(16_000);
            visibility.Refresh();
            AssertSuppressed("on the disconnect receive frame");
            foreach (var row in tracked)
            {
                That(row.Renderer.Enabled, "releasing an inadmissible renderer hold cannot show the disconnected avatar");
                That(!row.Peer.HasPose && !row.Viewer.Presence.TryGetPose(subject.Id, out _),
                    "disconnected subject retains c0 gameplay pose refusal");
                That(!row.Viewer.Presence.TryResolvePlayerRef(row.Reference, out _)
                    && !row.Viewer.Presence.IsCurrentViewAvatar(row.Entity, row.Viewer.World!),
                    "disconnected subject fails reference and native view admission");
                That(ReferenceEquals(row.Viewer.AvatarOf(subject)!.Entity, row.Entity), "disconnect retains the exact historical avatar root");
            }
            rig.Frames(60);
            AssertSuppressed("during grace");
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
            visibility.Refresh();
            AssertSuppressed("on the rejoin receive frame");
            rig.Frames(30);
            AssertSuppressed("while rejoined without a fresh pose");
            That(ReferenceEquals(rules, rig.Host.PeerOf(subject)!.Plausibility) && rules.Anchor.Equals(anchor) && rules.Strikes == strikes,
                "view-only recovery never resets anti-teleport validation");
            subject.Paused = false;
            That(rig.RunUntil(() => tracked.All(row => row.Viewer.Presence.TryGetPose(subject.Id, out _)), 60),
                "host and observer both receive a fresh subject pose");
            visibility.Refresh();
            foreach (var row in tracked)
                That(row.Avatar.IsVisible && !row.Renderer.Enabled,
                    row.Viewer.Name + " resumes presence without undoing the admitted renderer hold");
            visibility.Dispose();
            foreach (var row in tracked)
            {
                That(row.Avatar.IsVisible && row.Renderer.Enabled && HasNametag(row.Viewer.Presence),
                    row.Viewer.Name + " can see the avatar and nametag after the hold ends");
                row.Viewer.SceneVersion++;
                That(!row.Viewer.Presence.IsCurrentViewAvatar(row.Entity, row.Viewer.World!), "retained view root refuses a replaced scene instance");
            }
            foreach (var node in rig.All) node.Runtime.Disconnect();
            Console.WriteLine("Presence view-root reconnect passed: grace visibility, renderer holds and gameplay lifetime invalidation.");
        }

        private sealed class Renderer
        {
            internal bool Enabled = true;
        }
    }
}
