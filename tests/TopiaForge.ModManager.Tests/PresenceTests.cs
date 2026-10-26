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
    /// Presence over the loopback network (WS3.10): players see each other as avatars that move smoothly where the players
    /// walk, go when a player leaves, stay through a lost link's grace, snap rather than streak on a teleport, and answer
    /// the session's avatar and pose lookups; the host's validated positions fail closed for motion ownership.
    /// </summary>
    internal static class PresenceTests
    {
        internal static void Run()
        {
            PlayersAppearMoveSmoothlyAndLeave();
            ALostLinkKeepsTheAvatarThroughItsGrace();
            TeleportsSnapAndRestsAreHeld();
            LookupsAnswerForEveryPlayer();
            ValidatedPositionsFailClosed();
            StatesTravelWithThePlayers();
            Console.WriteLine("PresenceTests passed.");
        }

        private static void PlayersAppearMoveSmoothlyAndLeave()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            var host = rig.Host;
            var first = rig.Players[0];
            var second = rig.Players[1];
            rig.Frames(10);

            // Each player arrived in the host's world and was placed on the ring around the host's spawn, nearest slots first.
            That(first.Backend.Teleports.Count == 1 && Near(first.Backend.Teleports[0].Feet, SpawnSlotPlanner.SlotOffset(0), 0.001f) && !first.Backend.Teleports[0].KeepFacing,
                "the first player stands on the ring's first slot (" + string.Join(", ", first.Backend.Teleports.Select(item => Show(item.Feet))) + ")");
            That(second.Backend.Teleports.Count == 1 && Near(second.Backend.Teleports[0].Feet, SpawnSlotPlanner.SlotOffset(1), 0.001f),
                "and the second on the next one");
            That(host.Backend.Teleports.Count == 0 && host.Counters.Placements == 2, "the host keeps the spawn itself");

            foreach (var viewer in rig.All)
            {
                foreach (var other in rig.All)
                {
                    if (ReferenceEquals(viewer, other)) continue;
                    var avatar = viewer.AvatarOf(other);
                    That(avatar != null, viewer.Name + " sees " + other.Name);
                    That(Near(avatar!.Feet, OnGrid(other.Feet), 0.01f), viewer.Name + " sees " + other.Name + " where it stands (" + Show(avatar.Feet) + " / " + Show(other.Feet) + ")");
                    That(avatar.Tint.Equals(PresenceTint.For(other.PeerOf(other)!.Index)) && !avatar.IsWearingBody, "in its own colour, as a capsule with no body offered");
                }
            }

            That(host.PeerOf(first)!.Index == 2 && host.PeerOf(second)!.Index == 3 && host.PeerOf(host)!.Index == 1 &&
                 first.PeerOf(second)!.Index == 3 && second.PeerOf(first)!.Index == 2, "every peer names the players by the host's indices");

            // The first player walks along +X at 4 m/s for two seconds, then stops.
            var walking = true;
            rig.BeforeFrame = () =>
            {
                if (walking) first.Step(0.064f, 0f, 0f);
            };
            var onHost = host.AvatarOf(first)!;
            var onSecond = second.AvatarOf(first)!;
            var hostStart = onHost.Trail.Count;
            var secondStart = onSecond.Trail.Count;
            rig.Frames(125);
            walking = false;
            var lagOnHost = first.Feet.X - onHost.Feet.X;
            var lagOnSecond = first.Feet.X - onSecond.Feet.X;
            That(lagOnHost > 0.1f && lagOnHost < 1.0f, "the host's avatar trails the walking player a little, as interpolation does (" + lagOnHost + " m)");
            That(lagOnSecond > 0.1f && lagOnSecond < 1.2f, "and so does the other player's, a relay further on (" + lagOnSecond + " m)");
            AssertSmooth(onHost.Trail.Skip(hostStart + 20).ToList(), "on the host");
            AssertSmooth(onSecond.Trail.Skip(secondStart + 20).ToList(), "on the other player");
            That(onHost.Speed > 3f && onHost.Speed < 5f && onSecond.Speed > 3f && onSecond.Speed < 5f,
                "the avatars walk at the player's pace (" + onHost.Speed + ", " + onSecond.Speed + " m/s)");
            That(rig.RunUntil(() => Near(onHost.Feet, OnGrid(first.Feet), 0.002f) && Near(onSecond.Feet, OnGrid(first.Feet), 0.002f), 60),
                "once the player stops, both avatars come to rest where it stands");
            rig.Frames(30);
            That(onHost.Speed < 0.1f && onSecond.Speed < 0.1f, "and stand still");

            // The second player leaves: its avatar goes on every other peer at once.
            var secondOnHost = host.AvatarOf(second)!;
            var secondOnFirst = first.AvatarOf(second)!;
            rig.Network.Leave(second.Control);
            rig.Frames(2);
            That(secondOnHost.Destroyed && secondOnFirst.Destroyed && host.AvatarOf(second) == null && first.AvatarOf(second) == null,
                "a player that leaves takes its avatar with it");
            That(!host.Presence.TryGetAvatar(second.Id, out _) && !first.Presence.TryGetPose(second.Id, out _), "and the lookups no longer name it");
            That(second.Presence.Realm == NetRealm.Offline && second.Backend.Alive().Count == 0, "the player that left shows nobody");
            That(host.Presence.Roster.Count == 2 && !host.Presence.Roster.TryGetIndex(second.Id, out _), "and its index rests");
            That(rig.All.All(node => node.Log.Lines.Count(line => line.StartsWith("error", StringComparison.Ordinal)) == 0), "nobody logged an error");
        }

        /// <summary>No step between two frames runs backwards or jumps more than twice a frame's walk.</summary>
        private static void AssertSmooth(List<NetVector3> trail, string where)
        {
            That(trail.Count > 60, "the avatar moved every frame " + where + " (" + trail.Count + ")");
            for (var index = 1; index < trail.Count; index++)
            {
                var step = trail[index].X - trail[index - 1].X;
                That(step >= -0.0005f && step <= 0.13f, "the avatar moves smoothly " + where + ": step " + index + " is " + step + " m");
                That(Math.Abs(trail[index].Z - trail[index - 1].Z) <= 0.0005f, "and straight " + where);
            }
        }

        private static void ALostLinkKeepsTheAvatarThroughItsGrace()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            var host = rig.Host;
            var first = rig.Players[0];
            var second = rig.Players[1];
            rig.Frames(10);
            var onHost = host.AvatarOf(first)!;
            var onSecond = second.AvatarOf(first)!;

            // The first player's link goes: the session lists it disconnected through its grace, and its avatar stays.
            rig.Network.LoseLink(first.Control);
            rig.Frames(60);
            That(!onHost.Destroyed && !onSecond.Destroyed && host.AvatarOf(first) == onHost && second.AvatarOf(first) == onSecond,
                "a player whose link was lost keeps its avatar on every peer through its grace");
            That(host.Presence.TryGetAvatar(first.Id, out var kept) && ReferenceEquals(kept, onHost.Entity), "and the lookups still name it");
            That(host.Presence.ValidatedPosition(first.Id) == null, "but the host vouches for no position of a player it cannot hear");

            // It finds its link again: it moved meanwhile, and the host measures it afresh rather than calling that a cheat.
            first.Step(40f, 0f, 0f);
            rig.Network.Restore(first.Control);
            rig.Frames(30);
            That(host.Counters.Corrections == 0 && Near(onHost.Feet, OnGrid(first.Feet), 0.01f) && Near(onSecond.Feet, OnGrid(first.Feet), 0.01f),
                "back on its link, its avatar goes where it is now, with no correction");
            That(first.Backend.Teleports.Count == 1, "and it is not placed on the ring again in the world it was placed in");
            var validated = host.Presence.ValidatedPosition(first.Id);
            That(validated.HasValue && Near(validated.Value, OnGrid(first.Feet), 0.001f), "the host vouches for it again");

            // Lost again, and its grace runs out: the session lets go of it, and its avatar goes.
            rig.Network.LoseLink(first.Control);
            rig.Frames(5);
            That(!onHost.Destroyed, "still there while its grace lasts");
            rig.Network.ExpireGrace(first.Control);
            rig.Frames(2);
            That(onHost.Destroyed && onSecond.Destroyed && !host.Presence.TryGetAvatar(first.Id, out _), "when its grace runs out the avatar goes everywhere");
        }

        private static void TeleportsSnapAndRestsAreHeld()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            var first = rig.Players[0];
            var second = rig.Players[1];
            rig.Frames(10);
            var onSecond = second.AvatarOf(first)!;
            var from = onSecond.Feet;

            // The player's own game moves it 300 m in one step, as a respawn does: it flags the teleport, the host allows it,
            // and the avatar snaps there instead of streaking across the world.
            first.Step(300f, 0f, 0f);
            var start = onSecond.Trail.Count;
            rig.Frames(30);
            That(rig.Host.Counters.Corrections == 0 && Near(onSecond.Feet, OnGrid(first.Feet), 0.01f), "a flagged teleport stands, and the avatar follows");
            var between = onSecond.Trail.Skip(start).Count(feet => feet.X > from.X + 5f && feet.X < first.Feet.X - 5f);
            That(between == 0, "it snaps there, never drawn in between (" + between + " frames were)");

            // A player that stands still sends a pose a second, and the host relays nothing for it once others know it rests.
            rig.Frames(40);
            var sent = first.Counters.PosesSent;
            var relayed = rig.Host.Counters.RelayedPoses;
            rig.Frames(250);
            var heartbeats = first.Counters.PosesSent - sent;
            That(heartbeats >= 3 && heartbeats <= 5, "at rest a player sends one pose a second (" + heartbeats + " in 4 s)");
            That(rig.Host.Counters.RelayedPoses == relayed, "and the host relays none of it (" + (rig.Host.Counters.RelayedPoses - relayed) + ")");
            That(!onSecond.Destroyed && Near(onSecond.Feet, OnGrid(first.Feet), 0.01f), "while the avatar stays where the player rests");
        }

        private static void LookupsAnswerForEveryPlayer()
        {
            var rig = new PresenceRig();
            That(!rig.Host.Presence.TryGetAvatar(rig.Host.Id, out _) && !rig.Host.Presence.TryGetPose(rig.Host.Id, out _), "offline, nobody has an avatar or a pose");
            rig.HostWith(1);
            var host = rig.Host;
            var player = rig.Players[0];
            host.Backend.View = new NetQuaternion(0f, 0.70710677f, 0f, 0.70710677f);
            host.Backend.Crouching = true;
            rig.Frames(10);

            That(host.Presence.TryGetAvatar(host.Id, out var own) && ReferenceEquals(own, host.Backend.LocalPlayerEntity),
                "a peer's own player is its own entity");
            That(player.Presence.TryGetAvatar(host.Id, out var avatar) && ReferenceEquals(avatar, player.AvatarOf(host)!.Entity),
                "another player's is the avatar this peer shows");
            That(player.Presence.TryGetPose(host.Id, out var pose), "and its pose is known");
            That(Math.Abs(pose.Position.X) < 0.01f && Math.Abs(pose.Position.Z) < 0.01f && pose.Aim.Direction.X > 0.99f && Math.Abs(pose.Aim.Origin.Y - 1.6f) < 0.01f,
                "the pose stands where the host does and aims where it looks, from its eyes (" + pose.Aim.Direction.X + ")");
            That((pose.Flags & MultiplayerPoseFlags.Crouching) != 0, "crouching as it crouches");
            That(!player.Presence.TryGetPose(new ParticipantId("nobody"), out _) && !player.Presence.TryGetAvatar(new ParticipantId("nobody"), out _),
                "a participant the session does not list has neither");

            // A typing player's pose says so; the chat overlay sets it, and nothing else does.
            player.Presence.LocalTyping = true;
            rig.Frames(10);
            That(host.Presence.TryGetPose(player.Id, out var typing) && (typing.Flags & MultiplayerPoseFlags.Typing) != 0, "a player typing in chat shows it");
            player.Presence.LocalTyping = false;
            rig.Frames(10);
            That(host.Presence.TryGetPose(player.Id, out typing) && (typing.Flags & MultiplayerPoseFlags.Typing) == 0, "and stops showing it");

            // Talking is what the voice service says: the player's own transmitting, checked by what the host's relay heard.
            var playerVoice = new FakeVoice { State = InternalVoiceTransmitState.Transmitting };
            var hostVoice = new FakeVoice();
            player.Control.Voice = playerVoice;
            rig.Network.Host.Voice = hostVoice;
            player.Step(0.5f, 0f, 0f);
            rig.Frames(10);
            That(host.Presence.TryGetPose(player.Id, out var talking) && (talking.Flags & MultiplayerPoseFlags.Talking) == 0 && host.Counters.FlagsRefused > 0,
                "a player that says it talks while the host's relay hears nothing is not shown talking");
            hostVoice.Speaking.Add(player.Id);
            player.Step(0.5f, 0f, 0f);
            rig.Frames(10);
            That(host.Presence.TryGetPose(player.Id, out talking) && (talking.Flags & MultiplayerPoseFlags.Talking) != 0, "heard by the host's relay, it is");
            That(player.Presence.TryGetPose(player.Id, out var mine) && (mine.Flags & MultiplayerPoseFlags.Talking) != 0, "and its own pose says so too");
        }

        private static void ValidatedPositionsFailClosed()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            var host = rig.Host;
            var first = rig.Players[0];
            var second = rig.Players[1];
            first.Step(3f, 0f, 1f);
            rig.Frames(10);

            var validated = host.Presence.ValidatedPosition(first.Id);
            That(validated.HasValue && Near(validated.Value, OnGrid(first.Feet), 0.001f), "the host vouches for where a player last stood within the rules");
            That(host.Presence.ValidatedPosition(host.Id).HasValue && Near(host.Presence.ValidatedPosition(host.Id)!.Value, OnGrid(host.Feet), 0.001f),
                "and for its own player as sampled");
            That(host.Presence.ValidatedPosition(new ParticipantId("nobody")) == null, "never for a participant the session does not list");
            That(first.Presence.ValidatedPosition(second.Id) == null && first.Presence.ValidatedPosition(first.Id) == null, "and a joined player vouches for nobody");

            // A player whose poses stop coming goes stale: a check that needs a position fails rather than trusting an old one.
            first.Paused = true;
            rig.Frames(170);
            That(host.Presence.ValidatedPosition(first.Id) == null, "a player not heard for 2.5 s has no validated position");
            first.Paused = false;
            rig.Frames(10);
            That(host.Presence.ValidatedPosition(first.Id).HasValue, "and has one again once it is heard");

            // A forged position never becomes the validated one.
            var before = host.Presence.ValidatedPosition(first.Id)!.Value;
            var raw = rig.AddRawPlayer("forger");
            rig.Frames(6);
            raw.SendPose(new NetVector3(2f, 0f, 2f));
            rig.Frames(2);
            var honest = host.Presence.ValidatedPosition(raw.Id);
            That(honest.HasValue && Near(honest.Value, new NetVector3(2f, 0f, 2f), 0.01f), "a raw player's first pose stands");
            raw.SendPose(new NetVector3(900f, 0f, 2f));
            rig.Frames(2);
            var forged = host.Presence.ValidatedPosition(raw.Id);
            That(forged.HasValue && Near(forged.Value, new NetVector3(2f, 0f, 2f), 0.01f) && host.Counters.Corrections == 1,
                "a pose 900 m on in a frame is corrected, and the validated position stays where the player last stood within the rules");
            That(Near(host.Presence.ValidatedPosition(first.Id)!.Value, before, 0.001f), "nobody else's moves");
            host.Backend.HasPlayer = false;
            rig.Frames(2);
            That(host.Presence.ValidatedPosition(host.Id) == null, "a host with no player of its own vouches for none");
        }

        private static void StatesTravelWithThePlayers()
        {
            var rig = new PresenceRig();
            rig.Host.Backend.Health = 75f;
            rig.Host.Backend.HeldItem = "item.wrench";
            rig.HostWith(2);
            var first = rig.Players[0];
            var second = rig.Players[1];
            first.Backend.Health = 40f;
            first.Backend.HeldItem = "item.lamp";
            rig.Frames(30);

            That(first.Presence.TryGetPose(rig.Host.Id, out var hostPose) && hostPose.Health == 75f && hostPose.MaximumHealth == 100f && hostPose.HeldItemId == "item.wrench",
                "a joined player knows the host's health and what it holds");
            That(second.Presence.TryGetPose(first.Id, out var firstPose) && firstPose.Health == 40f && firstPose.HeldItemId == "item.lamp",
                "and every other player's, through the host");
            That(rig.Host.Presence.TryGetPose(first.Id, out var checkedPose) && checkedPose.Health == 40f, "as the host checked it");

            // A change travels on its own, within half a second: read four times a second, relayed ten times.
            first.Backend.HeldItem = null;
            first.Backend.Health = 35f;
            rig.Frames(30);
            That(second.Presence.TryGetPose(first.Id, out firstPose) && firstPose.Health == 35f && firstPose.HeldItemId == null, "dropping the lamp and losing health show at once");

            // Healing faster than any game regenerates is the host's to refuse: the most regeneration allows stands instead,
            // and rises toward the report at that pace with no report more, so an honest player's catches up.
            first.Backend.Health = 100f;
            That(rig.RunUntil(() => rig.Host.Counters.StatesRefused > 0, 40), "health that comes back faster than regeneration is refused");
            That(rig.Host.Presence.TryGetPose(first.Id, out checkedPose) && checkedPose.Health < 60f, "and held to it (" + checkedPose.Health + ")");
            rig.Frames(60);
            That(rig.Host.Presence.TryGetPose(first.Id, out checkedPose) && checkedPose.Health > 55f && checkedPose.Health < 100f,
                "it rises a quarter of the maximum a second (" + checkedPose.Health + " after a second)");
            rig.Frames(200);
            That(rig.Host.Presence.TryGetPose(first.Id, out checkedPose) && checkedPose.Health == 100f &&
                 second.Presence.TryGetPose(first.Id, out firstPose) && firstPose.Health == 100f, "and reaches the report, which every player then sees");
        }
    }
}
