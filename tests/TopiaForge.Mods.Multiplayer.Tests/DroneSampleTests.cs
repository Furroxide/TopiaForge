extern alias provider;

using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.DroneSample;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The drone sample spawns from its session hook: once for every session, and only where the process holds world
/// authority, so a host spawns again in a replaced session and a player spawns again once a join returns it to solo play.
/// </summary>
internal static class DroneSampleTests
{
    internal static void TheDroneSpawnsOnceInEverySessionTheHostHolds()
    {
        using var rig = MultiplayerTestRig.CreateListenServer();
        var host = new DroneSampleMod();
        using var hostBinding = Require(host.BindMultiplayer(rig.Server.Session), "the host binds");
        var first = host.GetDroneObjects();
        That(first.Count == 1 && first[0].OwnerId.Equals(rig.Server.ParticipantId),
            "binding to the session the host holds spawns one drone, which the host's player owns");

        var client = rig.AddRemoteClient("pilot");
        var pilot = new DroneSampleMod();
        using var pilotBinding = Require(pilot.BindMultiplayer(client.Session), "the client binds");
        rig.Flush();
        That(pilot.GetDroneObjects().Count == 1 && pilot.GetDroneObjects()[0].Id.Equals(first[0].Id) && host.GetDroneObjects().Count == 1,
            "a client spawns nothing of its own and sees the host's drone");

        rig.StartNewSession("second-round");
        rig.Flush();
        var second = host.GetDroneObjects();
        That(!first[0].IsSpawned && second.Count == 1 && second[0].OwnerId.Equals(rig.Server.ParticipantId),
            "a replaced session despawns the old drone and the hook spawns one for the new session");
        That(pilot.GetDroneObjects().Count == 1 && pilot.GetDroneObjects()[0].Id.Equals(second[0].Id), "which the client sees in turn");

        using var headless = MultiplayerTestRig.CreateDedicatedServer();
        var server = new DroneSampleMod();
        using var serverBinding = Require(server.BindMultiplayer(headless.Server.Session), "the dedicated server binds");
        That(server.GetDroneObjects().Count == 1 && !server.GetDroneObjects()[0].OwnerId.HasValue,
            "a server with no player of its own and nobody connected keeps the drone itself");
    }

    internal static void AFinishedJoinSpawnsTheDroneAgainInSoloPlay()
    {
        using var rig = new LiveRig(136);
        var host = new DroneSampleMod();
        Require(host.BindMultiplayer(rig.Host), "the host binds");
        That(host.GetDroneObjects().Count == 1, "the host spawns its drone in solo play");
        rig.StartHosting(new HostSettings("Drones", 4) { Port = 0 });
        var hostDrone = host.GetDroneObjects().Single();

        var player = rig.AddClient("player");
        var guest = new DroneSampleMod();
        Require(guest.BindMultiplayer(player), "the player binds");
        var soloDrone = guest.GetDroneObjects().Single();
        That(soloDrone.OwnerId.Equals(player.Snapshot.LocalParticipantId), "the player spawns its own drone in solo play too");

        var join = rig.Join(player, new JoinSettings("Player"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "the player joins (" + join.Failure + ")");
        That(!soloDrone.IsSpawned && guest.GetDroneObjects().Count == 1 && guest.GetDroneObjects()[0].Id.Equals(hostDrone.Id),
            "joining retires the solo drone, and the player sees the host's drone and spawns none");

        join.Leave();
        var back = guest.GetDroneObjects();
        That(player.Snapshot.HasWorldAuthority && back.Count == 1 && !ReferenceEquals(back[0], soloDrone) &&
             back[0].OwnerId.Equals(player.Snapshot.LocalParticipantId),
            "back in solo play the hook spawns a fresh drone, which the player owns");
        That(rig.Run(() => rig.Host.Snapshot.Participants.Count(participant => participant.IsConnected) == 1) &&
             host.GetDroneObjects().Count == 1 && hostDrone.IsSpawned,
            "and the host keeps its one drone");
    }

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        if (!result.TryGetValue(out var value)) throw new InvalidOperationException("Assertion failed: " + message + ": " + result.ErrorMessage);
        return value;
    }
}
