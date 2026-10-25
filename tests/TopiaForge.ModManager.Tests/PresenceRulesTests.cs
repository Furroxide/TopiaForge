using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.PresenceAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The host's rules end to end (WS3.10): a speed hack is corrected, each correction moving the cheat back, and the
    /// fifth strike removes it; a player corrected once that keeps to the rules afterwards plays on; poses a player sent
    /// before it heard a correction cost it nothing.
    /// </summary>
    internal static class PresenceRulesTests
    {
        internal static void Run()
        {
            ASpeedHackIsCorrectedThenKicked();
            APlayerCorrectedOncePlaysOn();
            Console.WriteLine("PresenceRulesTests passed.");
        }

        private static void ASpeedHackIsCorrectedThenKicked()
        {
            var rig = new PresenceRig();
            rig.HostWith(2);
            var host = rig.Host;
            var cheat = rig.Players[0];
            var watcher = rig.Players[1];
            rig.Frames(10);
            var placed = cheat.Backend.Teleports.Count;
            var startX = cheat.Feet.X;

            // The cheat runs at 60 m/s, twice the limit, in steps small enough that its own game never calls them teleports.
            rig.BeforeFrame = () => cheat.Step(0.96f, 0f, 0f);
            var start = rig.Clock.Now;
            var furthest = float.MinValue;
            var frames = 0;
            while (rig.Network.Kicks.Count == 0 && frames++ < 1500)
            {
                rig.Frame();
                var validated = host.Presence.ValidatedPosition(cheat.Id);
                if (validated.HasValue) furthest = Math.Max(furthest, validated.Value.X);
            }

            var seconds = (rig.Clock.Now - start) / 1_000_000.0;
            That(rig.Network.Kicks.Count == 1 && rig.Network.Kicks[0].Player.Equals(cheat.Id) && rig.Network.Kicks[0].Message == PresenceHost.KickMessage,
                "the host removes the cheat, after " + seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s");
            var moved = cheat.Backend.Teleports.Skip(placed).Count(teleport => teleport.KeepFacing);
            That(host.Counters.Corrections == 4 && moved == 4 && host.Counters.Kicks == 1,
                "after four corrections, each of which moved it back (" + host.Counters.Corrections + " corrections, " + moved + " moves), the fifth strike kicks");
            That(seconds > 1.8 && seconds < 4, "the first run lasts as long as the two-second bank holds out at twice the limit; a correction refills nothing, so the rest follow fast");
            That(furthest <= startX + 60f + 0.5f + 30f * (float)seconds, "and the host never vouched for a position past what the limit allows (" + furthest + ")");

            rig.BeforeFrame = null;
            rig.Frames(3);
            That(cheat.Presence.Realm == NetRealm.Offline && cheat.Backend.Alive().Count == 0, "the cheat is back in single player");
            That(watcher.AvatarOf(cheat) == null && host.AvatarOf(cheat) == null && !host.Presence.TryGetAvatar(cheat.Id, out _), "and its avatar is gone everywhere");
            That(host.Log.Lines.Any(line => line.Contains("removed because its movement kept breaking", StringComparison.Ordinal)) &&
                 rig.All.All(node => node.Log.Lines.All(line => !line.Contains(cheat.Id.Value, StringComparison.Ordinal))),
                "the host logs the removal, naming nobody");
            That(watcher.AvatarOf(host) != null && host.AvatarOf(watcher) != null && watcher.Presence.Realm == NetRealm.Client, "and the others play on");
        }

        private static void APlayerCorrectedOncePlaysOn()
        {
            var rig = new PresenceRig();
            rig.HostWith(1);
            var host = rig.Host;
            var player = rig.Players[0];
            rig.Frames(10);

            // A glitch in the player's game carries it at 45 m/s for five seconds, never far enough in a frame to be a teleport.
            var glitching = true;
            rig.BeforeFrame = () => player.Step(glitching ? 0.72f : 0.08f, 0f, 0f);
            That(rig.RunUntil(() => host.Counters.Corrections == 1, 400), "the host corrects the run once its bank runs dry");
            var anchor = host.PeerOf(player)!.Plausibility!.Anchor;
            That(rig.RunUntil(() => player.Backend.Teleports.Count(teleport => teleport.KeepFacing) == 1, 10), "the correction reaches the player");
            That(Near(player.Backend.Teleports.Last().Feet, NetQuantization.DequantizePosition(anchor), 0.001f),
                "which goes back to where the host last vouched for it");
            That(player.PeerOf(player) != null && rig.Host.Presence.Peers.Count == 2, "and stays in the session");
            glitching = false;

            // From then on it walks at 5 m/s: every pose stands, its strike is forgiven, and it is never removed.
            var corrections = host.Counters.Corrections;
            rig.Frames(800);
            That(host.Counters.Corrections == corrections && rig.Network.Kicks.Count == 0, "keeping to the rules afterwards costs nothing more");
            That(host.PeerOf(player)!.Plausibility!.StrikesAt(rig.Clock.Now) == 0, "and the strike is forgiven after ten seconds");
            var validated = host.Presence.ValidatedPosition(player.Id);
            That(validated.HasValue && Near(validated.Value, player.Feet, 0.5f), "the host vouches for where it walks");
            That(host.AvatarOf(player) != null && player.AvatarOf(host) != null, "and both still see each other");
        }
    }
}
