using System;
using System.Linq;
using System.Reflection;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.PresenceAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Avatars and their bodies (WS3.10): an avatar wears the body a first-party provider offers, in the player's colour,
    /// and the capsule when none can be made; a body that fails is asked for again, one the game destroyed or a scene
    /// change took is replaced, an avatar the game destroyed is made again, and one the engine cannot make is retried
    /// without breaking the frame. Nametags carry a display name and a place, and nothing else.
    /// </summary>
    internal static class PresenceAvatarTests
    {
        internal static void Run()
        {
            BufferedPosesWaitForTheCommittedWorld();
            AvatarsWearTheOfferedBody();
            BodiesThatFailAreAskedForAgain();
            AvatarsTheGameDestroysAreMadeAgain();
            NametagsCarryNamesOnly();
            Console.WriteLine("PresenceAvatarTests passed.");
        }

        private static RuntimeReplication Seam(PresenceNode node, OwnerModLifetime lifetime) =>
            node.Replication.Bind("io.example.robots", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), lifetime);

        private static void BufferedPosesWaitForTheCommittedWorld()
        {
            var rig = new PresenceRig();
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("loading-player");
            player.World = null;
            player.Phase = SessionPhase.LoadingWorld;
            var bodies = new FakeBodyFactory();
            That(Seam(player, new OwnerModLifetime()).RegisterAvatarBodyFactory(bodies).Succeeded, "the body provider is available throughout loading");
            rig.Join(player);
            rig.Frames(10);
            var host = player.PeerOf(rig.Host);
            That(host?.HasPose == true && host.Buffer != null && host.Buffer.Count > 0, "the host's genuine relayed pose is buffered before world readiness");
            That(player.AvatarOf(rig.Host) == null && player.Backend.Avatars.Count == 0 && bodies.Requests == 0,
                "buffered poses during loading create neither physical avatars nor native cosmetic bodies");

            player.World = rig.Host.World;
            player.Phase = SessionPhase.Running;
            That(rig.RunUntil(() => player.AvatarOf(rig.Host)?.IsWearingBody == true, 20), "the committed world admits its avatar and body without a new connection");
            var previous = player.AvatarOf(rig.Host)!;
            That(bodies.Made.Count == 1, "readiness constructs one body");

            player.World = null;
            player.Phase = SessionPhase.Stopping;
            player.SceneVersion++;
            rig.Frames(1);
            That(previous.Destroyed && bodies.Made[0].Disposed && player.AvatarOf(rig.Host) == null,
                "leaving the committed world retires the previous physical avatar and body");
            rig.Frames(20);
            That(bodies.Requests == 1 && player.Backend.Avatars.Count == 1,
                "continued host traffic cannot immediately recreate an avatar during the replacement load");

            player.World = rig.Host.World;
            player.Phase = SessionPhase.Running;
            That(rig.RunUntil(() => player.AvatarOf(rig.Host)?.IsWearingBody == true, 20), "a subsequent committed world reconstructs the presentation");
            That(!ReferenceEquals(previous, player.AvatarOf(rig.Host)) && bodies.Made.Count == 2 && player.Backend.Avatars.Count == 2,
                "the new world gets a fresh independently owned avatar and body");

            // A native level can have trusted poses without an authored world session. Null world identity is
            // not a loading signal: real ModRuntime reports Idle there, and Players host placement can establish trust.
            player.World = null;
            player.Phase = SessionPhase.Idle;
            That(rig.RunUntil(() => player.AvatarOf(rig.Host)?.IsWearingBody == true && bodies.Made.Count == 3, 20),
                "a settled native level without an authored session retains ordinary trusted peer avatars");
            foreach (var phase in new[] { SessionPhase.Preparing, SessionPhase.LoadingWorld, SessionPhase.StartingMode, SessionPhase.Stopping, SessionPhase.Running })
            {
                player.Phase = phase;
                rig.Frames(2);
                That(player.AvatarOf(rig.Host) == null && bodies.Requests == 3,
                    "uncommitted or invalidated readiness cannot recreate native bodies in phase " + phase);
            }
            player.World = rig.Host.World;
            foreach (var phase in new[] { SessionPhase.Preparing, SessionPhase.LoadingWorld, SessionPhase.StartingMode, SessionPhase.Stopping })
            {
                player.Phase = phase;
                rig.Frames(2);
                That(player.AvatarOf(rig.Host) == null && bodies.Requests == 3,
                    "a stale non-null world identity cannot bypass a transition phase " + phase);
            }
            player.World = null;
            player.Phase = SessionPhase.Idle;
            That(rig.RunUntil(() => player.AvatarOf(rig.Host)?.IsWearingBody == true && bodies.Made.Count == 4, 20),
                "finishing a native scene operation restores ordinary no-session presentation");
        }

        private static void AvatarsWearTheOfferedBody()
        {
            var rig = new PresenceRig();
            var hostBodies = new FakeBodyFactory();
            var lifetime = new OwnerModLifetime();
            var registration = Seam(rig.Host, lifetime).RegisterAvatarBodyFactory(hostBodies);
            That(registration.Succeeded, "a first-party provider offers bodies for avatars offline, as it loads");
            That(Seam(rig.Host, new OwnerModLifetime()).RegisterAvatarBodyFactory(new FakeBodyFactory()).ErrorCode == ModErrorCode.Conflict, "one provider makes them at a time");
            var playerBodies = new FakeBodyFactory();
            rig.HostWith(1);
            var player = rig.Players[0];
            Seam(player, new OwnerModLifetime()).RegisterAvatarBodyFactory(playerBodies);
            rig.Frames(10);

            var onHost = rig.Host.AvatarOf(player)!;
            var onPlayer = player.AvatarOf(rig.Host)!;
            That(onHost.IsWearingBody && onPlayer.IsWearingBody && hostBodies.Made.Count == 1 && playerBodies.Made.Count == 1, "each peer dresses its avatars in the offered body");
            var tint = PresenceTint.For(rig.Host.PeerOf(player)!.Index);
            That(hostBodies.Made[0].Request.Red == tint.Red && hostBodies.Made[0].Request.Green == tint.Green && hostBodies.Made[0].Request.Blue == tint.Blue,
                "in the player's colour");
            That(rig.Host.Counters.BodiesWorn == 1, "and counts it");

            // The body walks as the avatar does.
            rig.BeforeFrame = () => player.Step(0.05f, 0f, 0f);
            rig.Frames(60);
            That(hostBodies.Made[0].Speed > 2.5f && hostBodies.Made[0].Speed < 3.8f, "the body walks at the player's pace (" + hostBodies.Made[0].Speed + " m/s)");
            rig.BeforeFrame = null;

            // Withdrawn, the provider's bodies stay worn; new avatars wear the capsule.
            registration.Value!.Dispose();
            That(rig.Host.Replication.AvatarBodyFactory == null, "a withdrawn provider makes no more");
            var second = rig.AddPlayer("p2");
            rig.Join(second);
            That(rig.Host.AvatarOf(second) != null && !rig.Host.AvatarOf(second)!.IsWearingBody, "and a new avatar wears the capsule");

            // Leaving takes the body with the avatar.
            rig.Network.Leave(player.Control);
            rig.Frames(2);
            That(hostBodies.Made[0].Disposed && onHost.Destroyed && onPlayer.Destroyed && playerBodies.Made.All(body => body.Disposed), "an avatar that goes takes its body");
        }

        private static void BodiesThatFailAreAskedForAgain()
        {
            var rig = new PresenceRig();
            var bodies = new FakeBodyFactory { Available = false };
            Seam(rig.Host, new OwnerModLifetime()).RegisterAvatarBodyFactory(bodies);
            rig.HostWith(1);
            var player = rig.Players[0];
            rig.Frames(5);
            var avatar = rig.Host.AvatarOf(player)!;
            That(!avatar.IsWearingBody && bodies.Requests == 1, "with no robot to copy here, the avatar wears its capsule");

            // Asked again five seconds later, not every frame.
            rig.Frames(200);
            That(bodies.Requests == 1, "not asked again every frame");
            That(rig.RunUntil(() => bodies.Requests == 2, 200) && !avatar.IsWearingBody, "but again within five seconds");
            bodies.Throws = true;
            That(rig.RunUntil(() => bodies.Requests == 3, 400) && !avatar.IsWearingBody, "a provider that throws is a failure like any other");
            rig.Frames(2);
            var warned = rig.Host.Log.Lines.Count(line => line.StartsWith("warn", StringComparison.Ordinal) && line.Contains("InvalidOperationException", StringComparison.Ordinal));
            That(warned == 1 && rig.Host.Log.Lines.All(line => !line.StartsWith("error", StringComparison.Ordinal)), "reported once as a warning, breaking nothing");
            bodies.Throws = false;
            bodies.Available = true;
            That(rig.RunUntil(() => avatar.IsWearingBody, 400) && bodies.Made.Count == 1, "once one can be made, the avatar wears it");

            // A body the game destroyed comes off and another is made at once; so does a scene change.
            ((FakeEntity)bodies.Made[0].Root).IsAlive = false;
            rig.Frames(2);
            That(bodies.Made[0].Disposed && bodies.Made.Count == 2 && avatar.IsWearingBody, "a body the game destroyed is replaced");
            rig.Host.SceneVersion++;
            rig.Frames(1);
            That(bodies.Made[1].Disposed && avatar.Destroyed, "a scene change retires the old world's avatar before a new pose");
            That(rig.RunUntil(() => rig.Host.AvatarOf(player)?.IsWearingBody == true, 100) && bodies.Made.Count == 3,
                "a fresh pose in the new scene dresses a new avatar");
        }

        private static void AvatarsTheGameDestroysAreMadeAgain()
        {
            var rig = new PresenceRig();
            rig.HostWith(1);
            var player = rig.Players[0];
            rig.Frames(5);
            var avatar = rig.Host.AvatarOf(player)!;
            avatar.Destroyed = true;
            rig.Frames(2);
            var again = rig.Host.AvatarOf(player);
            That(again != null && !ReferenceEquals(again, avatar) && rig.Host.Counters.AvatarsMade == 2, "an avatar the game destroyed, as a scene unload might, is made again");

            // An engine that cannot make avatars is reported once and asked again each second; the frame goes on.
            rig.Host.Backend.FailAvatars = true;
            again!.Destroyed = true;
            rig.Frames(130);
            var errors = rig.Host.Log.Lines.Count(line => line.StartsWith("error", StringComparison.Ordinal) && line.Contains("avatar could not be made", StringComparison.Ordinal));
            That(errors == 1 && rig.Host.AvatarOf(player) == null, "an avatar that cannot be made is reported once (" + errors + ")");
            That(player.AvatarOf(rig.Host) != null && rig.Host.Presence.ValidatedPosition(player.Id).HasValue, "and nothing else stops");
            rig.Host.Backend.FailAvatars = false;
            rig.Frames(70);
            That(rig.Host.AvatarOf(player) != null, "once the engine can, the avatar is back within a second");
        }

        private static void NametagsCarryNamesOnly()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            var first = rig.Players[0];
            IPresenceNametagSource source = first.Presence;
            rig.Frames(10);
            That(source.NametagCount == 2, "a player has a nametag for each other player shown");
            var names = Enumerable.Range(0, source.NametagCount).Select(slot => source.TryGetNametag(slot, out var tag) ? tag : default).ToList();
            That(names.Select(tag => tag.DisplayName).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(new[] { "Bolt", "Widget" }),
                "by the names the players chose (" + string.Join(", ", names.Select(tag => tag.DisplayName)) + ")");
            var hostTag = names.Single(tag => tag.DisplayName == "Bolt");
            That(Near(hostTag.Anchor, first.AvatarOf(rig.Host)!.HeadPosition, 0.001f), "drawn above each avatar's head");
            That(!source.TryGetNametag(2, out _) && !source.TryGetNametag(-1, out _), "and nothing past them");

            var version = source.NametagVersion;
            rig.Network.Leave(rig.Players[1].Control);
            rig.Frames(2);
            That(source.NametagVersion != version && source.NametagCount == 1, "the set's version moves when a player goes");

            // The seam carries a display name and a place, never an id or an address.
            var fields = typeof(PresenceNametag).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal);
            That(fields.SequenceEqual(new[] { "Anchor", "DisplayName" }), "a nametag carries its name and its place only");
            var counters = typeof(PresenceCounters).GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            That(counters.Length > 10 && counters.All(property => property.PropertyType == typeof(long)), "presence's diagnostics are counts only");
        }
    }
}
