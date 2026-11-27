using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class ReplicaMotionRejectionTests
    {
        internal static void Run()
        {
            var rig = AuthorityRig.Create(players: 1);
            rig.Host.World.Proxies.IsSharedMod = mod => mod == WorldPeer.ModId || mod == "io.example.robot-provider";
            var host = new FirstPartyRobots(rig.Host);
            var guest = new FirstPartyRobots(rig.Players[0]);
            var owner = new AuthorityMod(rig.Players[0], ModExecutionContext.Participant);
            var robot = guest.Provider.Spawn(owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero, name: "Chaser")).Value!;
            var target = guest.Provider.Spawn(owner.Seam, new RobotAgentSpawnRequest(new Vec3(4, 0, 0), name: "Target")).Value!;
            owner.Lifetime.Track(robot); owner.Lifetime.Track(target);
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 2), "both robots adopted");
            var canonical = host.Agents.Values.Single(agent => agent.Name == "Chaser");
            var canonicalTarget = host.Agents.Values.Single(agent => agent.Name == "Target");
            Check(robot.MoveTo(new Vec3(2, 0, 0)).Succeeded && rig.Run(() => robot.HasReachedTarget), "initial authoritative arrival");
            var beforeChase = host.Provider.Describe(canonical);
            var refusals = rig.Host.Counters().ChangesRefused;

            Check(robot.Chase(target).Succeeded && !robot.HasReachedTarget && !robot.IsMoving, "live guest target queues Chase and its wait");
            // No frame has delivered the Chase yet. The host's target now dies while the guest still sees it alive.
            canonicalTarget.Despawn();
            Check(target.IsAlive && !canonicalTarget.IsAlive, "canonical target despawn occurs between guest queue and host execution");
            guest.Provider.ApplyReplica(guest.Agents[robot.Id], beforeChase);
            Check(!robot.HasReachedTarget, "older successful arrival cannot settle the queued Chase");
            Check(rig.Run(() => rig.Host.Counters().ChangesRefused > refusals), "host refuses the raced Chase");
            Check(canonical.Chased == null && canonical.HasReachedTarget && !canonical.IsMoving, "refusal preserves authoritative movement");
            Check(rig.Run(() => robot.HasReachedTarget && !robot.IsMoving), "refused Chase settles its token and restores authoritative observations");
            Check(guest.Agents[robot.Id].Inner.MovementTarget == null, "refusal never enables replica navigation");

            var settledRefusal = host.Provider.Describe(canonical);
            canonical.Inner.AutoCompleteMovement = false;
            Check(robot.MoveTo(new Vec3(8, 0, 0)).Succeeded && !robot.HasReachedTarget, "follow-on movement has a new wait");
            guest.Provider.ApplyReplica(guest.Agents[robot.Id], settledRefusal);
            Check(!robot.HasReachedTarget && !robot.IsMoving, "previous refusal cannot settle a newer successful command");
            Check(rig.Run(() => canonical.IsMoving && robot.IsMoving), "follow-on command executes and is observed");
            canonical.Inner.AutoCompleteMovement = true;
            canonical.Inner.MoveTo(new Vec3(8, 0, 0));
            Check(rig.Run(() => robot.HasReachedTarget && !robot.IsMoving), "follow-on arrival is observed");

            Check(robot.MoveTo(new Vec3(12, 0, 0)).Succeeded && !robot.HasReachedTarget, "new in-flight wait");
            var baselines = owner.Peer.Counters().BaselinesApplied;
            rig.Network.Rejoin(owner.Peer.Control);
            Check(rig.Run(() => owner.Peer.Counters().BaselinesApplied > baselines && robot.HasReachedTarget), "stream retirement clears the dropped wait");
            Check(canonical.Inner.MovementTarget!.Value.X == 8, "retired stream does not replay its movement");
            Console.WriteLine("Replica motion refusal: target-despawn race, stale success/refusal, follow-on movement and stream retirement passed.");
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Replica motion refusal: " + message); }
    }
}
