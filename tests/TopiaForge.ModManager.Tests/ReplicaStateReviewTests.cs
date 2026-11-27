using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class ReplicaStateReviewTests
    {
        internal static void Run()
        {
            var failed = 0;
            foreach (var test in new Action[] { Movement, Configuration, HostReference, RejoinedReference, RecoveryPose })
            {
                try { test(); Console.WriteLine(test.Method.Name + ": PASS"); }
                catch (Exception error) { failed++; Console.WriteLine(test.Method.Name + ": FAIL: " + error.Message); }
            }
            if (failed != 0) throw new InvalidOperationException("Replica state regressions failed: " + failed);
        }

        private static (WorldRig Rig, FirstPartyRobots Host, FirstPartyRobots Guest, AuthorityMod Owner, IRobotAgent Robot) Robots()
        {
            var rig = AuthorityRig.Create(players: 1);
            rig.Host.World.Proxies.IsSharedMod = mod => mod == WorldPeer.ModId || mod == "io.example.robot-provider";
            var host = new FirstPartyRobots(rig.Host);
            var guest = new FirstPartyRobots(rig.Players[0]);
            var owner = new AuthorityMod(rig.Players[0], ModExecutionContext.Participant);
            var robot = guest.Provider.Spawn(owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            owner.Lifetime.Track(robot);
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 1), "adoption completes");
            return (rig, host, guest, owner, robot);
        }

        private static void Movement()
        {
            var (rig, host, guest, _, robot) = Robots();
            var canonical = host.Agents.Values.Single();
            canonical.Inner.AutoCompleteMovement = false;
            var objective = new ObjectiveRunner(robot, RobotObjective.GoTo(new Vec3(4, 0, 0)), _ => null, () => 0);
            objective.Step();
            Check(rig.Run(() => robot.IsMoving && canonical.IsMoving), "guest observes authoritative moving status");
            canonical.Inner.AutoCompleteMovement = true;
            canonical.Inner.MoveTo(new Vec3(4, 0, 0));
            Check(rig.Run(() => robot.HasReachedTarget && !robot.IsMoving), "arrival without another command reaches guest");
            objective.Step();
            Check(objective.State == RobotObjectiveState.Arrived, "guest GoTo completes");
            var patrol = new ObjectiveRunner(robot, RobotObjective.Patrol(new[] { new Vec3(6, 0, 0), new Vec3(8, 0, 0) }), _ => null, () => 10);
            patrol.Step(); patrol.Step();
            Check(patrol.State == RobotObjectiveState.Seeking, "old arrival does not complete the next command");
            Check(rig.Run(() => robot.HasReachedTarget), "immediate host arrival is acknowledged");
            patrol.Step();
            Check(patrol.State == RobotObjectiveState.Dwelling, "guest patrol reaches dwell");
            Check(guest.Agents[robot.Id].Inner.MovementTarget == null, "replica never starts locomotion");
        }

        private static void Configuration()
        {
            var (rig, host, guest, _, robot) = Robots();
            Check(robot.ConfigureMovement(new RobotMovementSettings(RobotGait.Run, 7, 180, 2)).Succeeded, "configuration accepted");
            Check(rig.Run(() => robot.MoveSpeed == 7 && robot.TurnSpeed == 180 && robot.StopDistance == 2 && robot.Gait == RobotGait.Run),
                "all observable settings update on the existing guest handle");
            Check(host.Agents.Values.Single().MoveSpeed == 7 && guest.Agents[robot.Id].Inner.MovementTarget == null,
                "host configured and replica remains passive");
        }

        private static (PresenceRig Rig, PresenceNode Guest, MultiplayerPlayers Host, MultiplayerPlayers Client) Players()
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
            var client = MultiplayerPlayers.Install(guest.Runtime, guest.Presence);
            guest.Replication.Players = client;
            guest.Runtime.Connect();
            rig.Join(guest); rig.Frames(24);
            return (rig, guest, host, client);
        }

        private static void HostReference()
        {
            var (rig, guest, host, client) = Players();
            Check(host.TryCreateReference(rig.Host.Backend.LocalPlayerEntity!, out var reference), "host has a reference without a persistence key");
            Check(client.TryResolveReference(reference, out var avatar) && avatar != null, "guest resolves host avatar");
            Check(client.TryCreateReference(avatar!, out var roundTrip) && roundTrip.Equals(reference), "guest can route Chase(hostAvatar)");
            Check(host.TryResolveReference(roundTrip, out var local) && ReferenceEquals(local, rig.Host.Backend.LocalPlayerEntity), "host resolves routed target locally");
            rig.Network.ReplaceSession(); rig.Frames(24);
            Check(!host.TryResolveReference(reference, out _) && !client.TryResolveReference(reference, out _), "replacement retires host reference on both peers");
        }

        private static void RejoinedReference()
        {
            var (rig, guest, host, client) = Players();
            Check(client.TryCreateReference(guest.Backend.LocalPlayerEntity!, out var reference), "guest reference created");
            Check(host.TryResolveReference(reference, out _), "host initially resolves guest");
            rig.Network.Leave(guest.Control); rig.Frames(3);
            Check(!host.TryResolveReference(reference, out _), "departed guest does not resolve");
            rig.Join(guest); rig.Frames(24);
            Check(!host.TryResolveReference(reference, out _) && !client.TryResolveReference(reference, out _),
                "same persistence key cannot revive a retired reference");
            Check(client.TryCreateReference(guest.Backend.LocalPlayerEntity!, out var next) && !next.Equals(reference) && host.TryResolveReference(next, out _),
                "rejoined guest has a shared fresh reference");
        }

        private static void RecoveryPose()
        {
            var (rig, host, guest, owner, robot) = Robots();
            var canonical = host.Agents.Values.Single();
            var replica = guest.Agents[robot.Id];
            rig.Frames(30);
            owner.Peer.Control.Stalled = true;
            canonical.Body.Position = new NetVector3(12, 0, 4);
            host.Provider.Execute(canonical, RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Name); RobotReplicationWire.Text(w, "Recovered", 128); }), _ => null);
            // Record the changed descriptor while the receiver is stalled, then discard the old stream.
            rig.Host.Replication.ExecuteEntityCommand(rig.Host.Runtime.Spawns.Find(canonical)!, RobotReplicationWire.Kind,
                RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Name); RobotReplicationWire.Text(w, "Recovered", 128); }));
            rig.Frames(30);
            rig.Network.Rejoin(owner.Peer.Control);
            owner.Peer.Control.Stalled = false;
            Check(rig.Run(() => replica.Name == "Recovered" && owner.Peer.World.Progress.Phase == NetworkWorldPhase.Synchronized), "changed baseline updates existing description");
            Check(replica.Body.Position.X == 12 && replica.Body.Position.Z == 4, "changed baseline immediately places resting body");
            Check(ReferenceEquals(replica, guest.Agents[robot.Id]), "recovery preserves body and handle identity");
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
