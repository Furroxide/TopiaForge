using System;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class FirstPartyPlayerReferenceTests
    {
        internal static void Run()
        {
            var rig = new PresenceRig();
            rig.Host.Runtime.Disconnect();
            var host = MultiplayerPlayers.Install(rig.Host.Runtime, rig.Host.Presence);
            rig.Host.Replication.Players = host;
            rig.Host.Runtime.Connect();
            rig.OpenToMultiplayer();
            var guest = rig.AddPlayer("guest");
            guest.Control.PlayerKey = new MultiplayerPlayerKey("persistent-guest");
            guest.Runtime.Disconnect();
            var local = MultiplayerPlayers.Install(guest.Runtime, guest.Presence);
            guest.Replication.Players = local;
            guest.Runtime.Connect();
            rig.Join(guest); rig.Frames(24);
            Check(guest.Backend.LocalPlayerEntity != null && local.TryCreateReference(guest.Backend.LocalPlayerEntity, out var key),
                "local player gets a canonical reference");
            guest.Replication.TryCreateRef(guest.Backend.LocalPlayerEntity!, out var reference);
            var resolved = rig.Host.Replication.TryResolveRef(reference, out var avatar);
            Check(reference.Kind == InternalNetRefKind.Player && resolved && avatar != null,
                "host resolves the same key to its remote avatar");
            Check(host.TryCreateReference(avatar!, out var roundTrip) && roundTrip.Equals(reference), "references round trip across peers");
            rig.Network.Leave(guest.Control); rig.Frames(3);
            Check(!rig.Host.Replication.TryResolveRef(reference, out _), "departed player does not resolve");
            rig.Join(guest); rig.Frames(24);
            Check(!rig.Host.Replication.TryResolveRef(reference, out _), "new player lifetime cannot consume a stale target");
            Console.WriteLine("First-party player references: 1 group passed.");
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Player reference: " + message); }
    }
}
