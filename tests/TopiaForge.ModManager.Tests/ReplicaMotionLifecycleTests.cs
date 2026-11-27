using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class ReplicaMotionLifecycleTests
    {
        internal static void Run()
        {
            var rig = AuthorityRig.Create(players: 1);
            rig.Host.World.Proxies.IsSharedMod = mod => mod == WorldPeer.ModId || mod == "io.example.robot-provider";
            var host = new FirstPartyRobots(rig.Host);
            var guest = new FirstPartyRobots(rig.Players[0]);
            var owner = new AuthorityMod(rig.Players[0], ModExecutionContext.Participant);
            var robot = guest.Provider.Spawn(owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            owner.Lifetime.Track(robot);
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 1), "adopted");
            Check(robot.MoveTo(new Vec3(3, 0, 0)).Succeeded && rig.Run(() => robot.HasReachedTarget), "first arrival");
            var canonical = host.Agents.Values.Single();
            var stale = host.Provider.Describe(canonical);
            Check(ReferenceEquals(stale, host.Provider.Describe(canonical)), "unchanged descriptions are cached");

            Check(robot.MoveTo(new Vec3(6, 0, 0)).Succeeded && !robot.HasReachedTarget, "new command awaits its own arrival");
            guest.Provider.ApplyReplica(guest.Agents[robot.Id], stale);
            Check(!robot.HasReachedTarget, "an older update in flight cannot complete the next move");
            var baselines = owner.Peer.Counters().BaselinesApplied;
            rig.Network.Rejoin(owner.Peer.Control); // Drops the unsent command before the host can apply it.
            Check(rig.Run(() => owner.Peer.Counters().BaselinesApplied > baselines && robot.HasReachedTarget),
                "recovery cancels the lost wait and exposes authoritative arrival");
            Check(canonical.Inner.MovementTarget!.Value.X == 3, "lost command was not replayed");

            Check(robot.ConfigureMovement(new RobotMovementSettings(RobotGait.Walk, 5, 90, 1)).Succeeded && rig.Run(() => robot.MoveSpeed == 5),
                "configuration acknowledged");
            var late = rig.AddPlayer("late"); var lateRobots = new FirstPartyRobots(late);
            rig.JoinAndSynchronize(late);
            var copy = lateRobots.Agents.Values.Single();
            Check(copy.HasReachedTarget && !copy.IsMoving && copy.MoveSpeed == 5 && copy.TurnSpeed == 90 &&
                  copy.StopDistance == 1 && copy.Gait == RobotGait.Walk && copy.Inner.MovementTarget == null,
                "late join receives status and settings without locomotion");

            var oldCommand = RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Name); RobotReplicationWire.Text(w, "Old", 128); });
            oldCommand[0] = 1;
            var name = canonical.Name;
            Check(!host.Provider.Execute(canonical, oldCommand, _ => null).Succeeded && canonical.Name == name,
                "version 1 commands are refused before effects");
            var missingToken = RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.MoveTo); RobotReplicationWire.Vector(w, new Vec3(9, 0, 0)); });
            Check(!host.Provider.Execute(canonical, missingToken, _ => null).Succeeded && canonical.Inner.MovementTarget!.Value.X == 3,
                "truncated movement acknowledgement token has no partial effect");
            var serverScript = new AuthorityMod(rig.Host);
            var guestScript = new AuthorityMod(owner.Peer);
            var request = new RobotAgentSpawnRequest(Vec3.Zero, name: "Scripted");
            var scripted = host.Provider.Spawn(serverScript.Seam, request).Value!;
            Check(scripted.MoveTo(new Vec3(2, 0, 0)).Succeeded &&
                  rig.Run(() => guest.Agents.Values.Any(agent => agent.IsAlive && agent.Name == "Scripted" && agent.HasReachedTarget)),
                "host's scripted robot arrives before a local shadow is created");
            var shadow = guest.Provider.Spawn(guestScript.Seam, request).Value!;
            Check(shadow.IsAlive && shadow.HasReachedTarget, "late shadow binding applies current authoritative status");
            var shadowMove = shadow.MoveTo(new Vec3(9, 0, 0));
            Check(shadowMove.TryGetValue(out var sent) && !sent && shadow.HasReachedTarget,
                "a no-op shadow command does not await an acknowledgement");
            Console.WriteLine("Replica motion lifecycle: recovery, stale updates, late join, shadow binding, cached descriptions and wire rejection passed.");
        }

        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Replica motion lifecycle: " + message); }
    }
}
