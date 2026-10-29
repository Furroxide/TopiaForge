using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class AvatarAppearanceTests
    {
        private static PlayersTestServices Bind(PresenceNode node)
        {
            node.Runtime.Disconnect();
            var runtime = MultiplayerPlayers.Install(node.Runtime, node.Presence);
            node.Runtime.Connect();
            return new PlayersTestServices(runtime, node.Control, node.Backend);
        }
        private static AvatarPresentation State(PresenceNode node, PresenceNode subject) => node.PeerOf(subject)!.Presentation!;
        private static void ReplicationAndRestoration()
        {
            var rig = new PresenceRig();
            using var host = Bind(rig.Host);
            var solo = host.Player(rig.Host.Id);
            using (var soloLook = Lease(solo.Appearance.Set(Red)))
                Check(solo.Appearance.TryGet(out var look) && Red.Equals(look), "offline same public path");
            rig.OpenToMultiplayer();
            var first = rig.AddPlayer("first");
            using var firstServices = Bind(first);
            rig.Join(first); rig.Frames(20);
            var target = host.Player(first.Id);
            Check(target.Appearance.TryGet(out var original), "host reads canonical original");
            var identity = target.Id; var key = target.Key; var display = target.DisplayName;
            var lookLease = Lease(target.Appearance.Set(Red));
            var nameLease = Lease(target.Appearance.SetNameplate("<b>Sam</b>"));
            var poseLease = Lease(target.Appearance.SetPoweredDown());
            rig.Frames(20);
            Check(State(first, first).Current.Equals(Red) && State(first, first).PoweredDown, "own presentation arrives on its client too; malformed=" + first.Counters.Malformed + "; forged=" + first.Counters.Forged + "; received=" + first.PeerOf(first)!.HasPresentation + "; version=" + rig.Host.PeerOf(first)!.PresentationVersion + "; sent=" + rig.Host.PeerOf(first)!.PresentationSent[rig.Host.PeerOf(first)!.Index] + "; identityMatch=" + (rig.Host.PeerOf(first)!.PresentationLifetime == first.PeerOf(first)!.PresentationLifetime));
            Check(rig.Host.AvatarOf(first)!.Appearance!.Equals(Red) && rig.Host.AvatarOf(first)!.PoweredDown, "host renders colour and powered-down pose");
            Check(rig.Host.Presence.TryGetNametag(0, out var tag) && tag.DisplayName == "<b>Sam</b>", "literal alias reaches automatic nametag source");
            Check(target.Id.Equals(identity) && target.Key.Equals(key) && target.DisplayName == display, "alias never changes participant identity or display name");
            var late = rig.AddPlayer("late");
            rig.Join(late); rig.Frames(30);
            Check(State(late, first).Current.Equals(Red) && State(late, first).PoweredDown && State(late, first).Name == "<b>Sam</b>", "late join receives all effective features");
            using (var hide = Lease(target.Appearance.SetNameplate(visible: false)))
            {
                rig.Frames(15);
                Check(!rig.Host.Presence.TryGetNametag(0, out _) && !State(late, first).Visible, "hide suppresses source and replicates");
            }
            rig.Frames(15);
            Check(State(late, first).Visible && State(late, first).Name == "<b>Sam</b>", "hide disposal reveals underlying alias");
            rig.Network.LoseLink(first.Control); rig.Frames(3);
            nameLease.Dispose(); poseLease.Dispose(); lookLease.Dispose();
            rig.Frames(15);
            rig.Network.Restore(first.Control); rig.Frames(35);
            Check(State(first, first).Current.Equals(original) && State(first, first).Name == null && !State(first, first).PoweredDown, "reconnect receives restored state even after missed disposal");
            using var replacement = Lease(target.Appearance.Set(Blue));
            var sceneAvatar = rig.Host.AvatarOf(first)!;
            rig.Host.SceneVersion++; rig.Frames(90);
            Check(!sceneAvatar.IsAlive && rig.Host.AvatarOf(first)!.Appearance!.Equals(Blue), "new scene avatar receives retained appearance");
            Lease(target.Appearance.SetNameplate("Round", false));
            Lease(target.Appearance.SetPoweredDown());
            rig.Frames(15);
            host.Dispose(); rig.Frames(25);
            foreach (var node in new[] { rig.Host, first, late })
                Check(State(node, first).Current.Equals(original) && State(node, first).Name == null && State(node, first).Visible && !State(node, first).PoweredDown,
                    "scope restores every feature on every client");

            // Isolate the new idle send/draw work. Other network stages already have their own exact-zero tests.
            rig.Host.Backend.RecordTrails = false;
            for (var i = 0; i < 200; i++) { rig.Host.Presence.DriveAvatars(rig.Clock.Read(), .016f); rig.Host.Presence.SendPresentations(rig.Host.Channel); rig.Clock.Advance(100_000); }
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++) { rig.Host.Presence.DriveAvatars(rig.Clock.Read(), .016f); rig.Host.Presence.SendPresentations(rig.Host.Channel); rig.Clock.Advance(100_000); }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Check(allocated == 0, "idle presentation allocates exactly zero bytes; actual=" + allocated);
            Console.WriteLine("Avatar presentation idle: " + allocated + " bytes; 200 warmups; 10,000 frames.");
        }
        private static void AuthorityAndIncarnations()
        {
            var rig = new PresenceRig();
            using var host = Bind(rig.Host);
            rig.OpenToMultiplayer();
            var first = rig.AddPlayer("first");
            using var client = Bind(first);
            rig.Join(first); rig.Frames(20);
            Check(client.Player(first.Id).Appearance.Set(Red).ErrorCode == ModErrorCode.NotAuthoritative, "public client cannot change its own replicated appearance");
            var target = host.Player(first.Id);
            host.Context.AppearanceProfile = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Participant);
            Check(target.Appearance.Set(Red).ErrorCode == ModErrorCode.NotAuthoritative, "listen-host participant context has no authority");
            host.Context.AppearanceProfile = new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.Process);
            Check(target.Appearance.Set(Red).ErrorCode == ModErrorCode.NotAuthoritative, "client-local context has no authority");
            host.Context.AppearanceProfile = new ModNetProfile(ModNetworkClass.ServerOnly, ModExecutionContext.GamemodeServer);
            using var applied = Lease(target.Appearance.Set(Red));
            var prior = rig.Host.PeerOf(first)!;
            var packet = new WorldWireWriter(PresencePresentationWire.MaximumBytes);
            PresencePresentationWire.Write(packet, rig.Host.Control.SessionSnapshot.Id.Value, prior);
            var oldWire = packet.Segment.ToArray();
            first.Control.PlayerKey = new MultiplayerPlayerKey("replacement-key");
            rig.Network.Rejoin(first.Control);
            rig.Frames(20);
            Check(!applied.IsActive && !target.IsPresent && target.Appearance.Set(Blue).ErrorCode == ModErrorCode.NotFound, "same id with a replacement key retires retained handles");
            Check(!State(rig.Host, first).Current.Equals(Red), "replacement cannot inherit old lease");
            first.Channel.Inject(rig.Host.Id, oldWire); rig.Frames(3);
            Check(!State(first, first).Current.Equals(Red), "old incarnation packet cannot dress replacement");
            var forgedBefore = rig.Host.Counters.Forged;
            first.Channel.Send(new ArraySegment<byte>(oldWire), InternalSystemTarget.Host); rig.Frames(3);
            Check(rig.Host.Counters.Forged > forgedBefore && !State(rig.Host, first).Current.Equals(Red), "client appearance payload is refused by host");
        }
    }
}
