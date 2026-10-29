using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class FirstPartyReplicationTests
    {
        internal static void Run()
        {
            ReplicaStateReviewTests.Run();
            ReplicaMotionLifecycleTests.Run();
            ReplicaMotionRejectionTests.Run();
            RobotSpeechTests.Run();
            FirstPartyPlayerReferenceTests.Run();
            FirstPartyBrainAuthorityTests.Run();
            FirstPartyLocalRobotTests.Run();
            SoloPromotesSameBody();
            HostOwnedRobots();
            HostAndGuestRobots();
            ChaseUsesCanonicalEntity();
            InvalidPayloadsDoNothing();
            ScopeAndDepartureCleanup();
            QuotaIsAutomatic();
            QueuedCommandsAreBoundedAndScoped();
            Console.WriteLine("First-party replication: 8 groups passed.");
        }

        private static void SoloPromotesSameBody()
        {
            var rig = new WorldRig();
            rig.UseScene(scene => scene.AddScene("Yard"));
            var host = new FirstPartyRobots(rig.Host);
            var owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var robot = Spawn(host, owner, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            Check(robot.MoveTo(new Vec3(2, 0, 0)).Succeeded && robot.SetName("Solo helper").Succeeded, "solo uses the same service");
            rig.OpenToMultiplayer();
            var joined = rig.AddPlayer("guest");
            var replicas = new FirstPartyRobots(joined);
            rig.JoinAndSynchronize(joined);
            Check(robot.IsAlive && host.Agents.Count == 1 && replicas.Agents.Values.Single().Name == "Solo helper",
                "opening solo to co-op promotes the existing canonical body and its current appearance");
        }

        private static void HostOwnedRobots()
        {
            var (rig, host, guest, _) = Setup();
            var owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var made = Spawn(host, owner, new RobotAgentSpawnRequest(new Vec3(3, 0, 2),
                facing: new Vec3(1, 0, 0), brainMode: RobotBrainMode.Autonomous, name: "Host helper"));
            Check(made.Succeeded && made.Value!.BrainMode == RobotBrainMode.Autonomous, "server owns its autonomous robot");
            Check(rig.Run(() => guest.Agents.Count == 1), "server spawn reaches the guest");
            var replica = guest.Agents.Values.Single();
            Check(replica.Replica && replica.BrainMode == RobotBrainMode.Dormant && replica.Request.Facing!.Value.X > 0.99f,
                "host facing survives replication without enabling the guest brain");
            Check(made.Value!.SetName("Host changed").Succeeded && rig.Run(() => replica.Name == "Host changed"),
                "server appearance edits reach the existing guest body");
            owner.Lifetime.Dispose();
            Check(rig.Run(() => !replica.IsAlive), "server scope cleanup reaches guests");
        }

        private static void ChaseUsesCanonicalEntity()
        {
            var (rig, host, guest, owner) = Setup();
            var robot = Spawn(guest, owner, new RobotAgentSpawnRequest(Vec3.Zero, name: "Chaser")).Value!;
            var target = Spawn(guest, owner, new RobotAgentSpawnRequest(new Vec3(2, 0, 0), name: "Target")).Value!;
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 2), "both chase bodies adopted");
            var canonical = host.Agents.Values.Single(agent => agent.Name == "Chaser");
            var canonicalTarget = host.Agents.Values.Single(agent => agent.Name == "Target");
            Check(robot.Chase(target).Succeeded && rig.Run(() => canonical.Chased != null), "guest chase reaches host");
            Check(canonical.Chased!.Id == canonicalTarget.Id && !ReferenceEquals(canonical.Chased, target),
                "host chases its own canonical entity, not a guest-local handle");
            target.Despawn();
            Check(rig.Run(() => !target.IsAlive && !canonicalTarget.IsAlive), "target lifetime ends on both peers");
            Check(!robot.Chase(target).Succeeded, "stale target cannot be chased");
        }

        private static (WorldRig Rig, FirstPartyRobots Host, FirstPartyRobots Guest, AuthorityMod Owner) Setup()
        {
            var rig = AuthorityRig.Create(players: 1);
            rig.Host.World.Proxies.IsSharedMod = mod => mod == WorldPeer.ModId || mod == "io.example.robot-provider";
            return (rig, new FirstPartyRobots(rig.Host), new FirstPartyRobots(rig.Players[0]),
                new AuthorityMod(rig.Players[0], ModExecutionContext.Participant));
        }

        private static void HostAndGuestRobots()
        {
            var (rig, host, guest, owner) = Setup();
            var made = Spawn(guest, owner, new RobotAgentSpawnRequest(new Vec3(1, 2, 3),
                brainMode: RobotBrainMode.Autonomous, name: "Helper"));
            Check(made.Succeeded, "a consumer's participant can spawn through the provider");
            var handle = made.Value!;
            Check(guest.Agents[handle.Id].Replica && handle.BrainMode == RobotBrainMode.Dormant, "prediction has no autonomous brain");
            Check(handle.SetName("Before adoption").Succeeded, "immediate commands wait automatically for adoption");
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 1), "spawn is adopted");
            var canonical = host.Agents.Values.Single();
            Check(!canonical.Replica && canonical.BrainMode == RobotBrainMode.Autonomous, "only the canonical body runs its brain");
            Check(rig.Run(() => canonical.Name == "Before adoption" && handle.Name == "Before adoption") && handle.IsAlive,
                "immediate mutation preserves the adopted handle");
            Check(handle.SetName("Changed").Succeeded && handle.SetScale(2).Succeeded, "commands are accepted");
            Check(rig.Run(() => canonical.Name == "Changed" && guest.Agents[handle.Id].Inner.Scale == 2), "commands update host and prediction in place");
            Check(handle.IsAlive && handle.Name == "Changed", "adopted handle survives appearance updates");
            var intruder = new AuthorityMod(owner.Peer, ModExecutionContext.Participant, "io.example.other");
            Check(!intruder.Seam.SubmitEntityCommand(guest.Agents[handle.Id], RobotReplicationWire.Kind,
                RobotReplicationWire.Write(w => w.Write((byte)RobotCommand.Despawn))).Succeeded, "another consumer cannot command it");
            var late = rig.AddPlayer("late"); var lateRobots = new FirstPartyRobots(late);
            rig.JoinAndSynchronize(late);
            Check(lateRobots.Agents.Values.Single().Name == "Changed" && lateRobots.Agents.Values.Single().Inner.Scale == 2,
                "late join receives the current appearance");
            Check(lateRobots.Agents.Values.Single().BrainMode == RobotBrainMode.Dormant, "late join never starts the host brain");
            var writer = new WorldWireWriter(4096);
            var forged = RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Scale); w.Write(9f); });
            EntityCommandWire.Write(writer, AuthorityTarget.Entity(new NetworkEntityId(guest.Agents[handle.Id].Body.Mark)),
                WorldPeer.ModId, RobotReplicationWire.Kind, forged);
            late.World.Channel.TrySendToHost(writer); rig.Frames(3);
            Check(canonical.Inner.Scale == 2, "host refuses another participant's forged target");

            Check(handle.Despawn().Succeeded, "owner requests despawn");
            Check(rig.Run(() => !canonical.IsAlive && !handle.IsAlive && !lateRobots.Agents.Values.Single().IsAlive), "despawn reaches every copy");
        }

        private static void InvalidPayloadsDoNothing()
        {
            var (rig, host, guest, owner) = Setup();
            var localOnly = new AuthorityMod(owner.Peer, ModExecutionContext.Participant, networkClass: ModNetworkClass.ClientLocal);
            Check(Spawn(guest, localOnly, new RobotAgentSpawnRequest(Vec3.Zero)).ErrorCode == ModErrorCode.NotAuthoritative && guest.Agents.Count == 0,
                "a client-local consumer is refused before making an unroutable body");
            Check(!Spawn(guest, owner, new RobotAgentSpawnRequest(new Vec3(float.NaN, 0, 0))).Succeeded,
                "non-finite spawn rejected before construction");
            Check(!Spawn(guest, owner, new RobotAgentSpawnRequest(Vec3.Zero, name: "<b>Robot</b>")).Succeeded,
                "markup is not accepted as a robot name");
            Check(!Spawn(guest, owner, new RobotAgentSpawnRequest(Vec3.Zero,
                interaction: RobotInteractionOptions.Custom(new RobotCustomInteraction("Inspect")))).Succeeded && guest.Agents.Count == 0,
                "client callbacks are refused before construction instead of silently discarded");
            var robot = Spawn(guest, owner, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 1), "valid spawn adopted");
            var canonical = host.Agents.Values.Single();
            var malformed = RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Kill); w.Write((byte)0); RobotReplicationWire.Text(w, "test", 128); w.Write((byte)99); });
            Check(!host.Provider.Execute(canonical, malformed, _ => null).Succeeded && canonical.IsAlive,
                "a malformed command tail has no partial side effect");
            var writer = new WorldWireWriter(4096);
            EntityCommandWire.Write(writer, AuthorityTarget.Entity(new NetworkEntityId(guest.Agents[robot.Id].Body.Mark)),
                WorldPeer.ModId, RobotReplicationWire.Kind, RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Scale); w.Write(3f); }));
            for (var i = 0; i < 200; i++) owner.Peer.World.Channel.TrySendToHost(writer);
            rig.Frames(20);
            Check(rig.Host.Counters().ChangesRefused > 0, "host budgets bound command floods");
        }

        private static void ScopeAndDepartureCleanup()
        {
            var (rig, host, guest, owner) = Setup();
            Spawn(guest, owner, new RobotAgentSpawnRequest(Vec3.Zero));
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 1), "spawn adopted before cleanup");
            var canonical = host.Agents.Values.Single();
            owner.Lifetime.Dispose();
            Check(rig.Run(() => !canonical.IsAlive), "participant scope cleanup removes its canonical robot");
            var next = new AuthorityMod(owner.Peer, ModExecutionContext.Participant);
            Spawn(guest, next, new RobotAgentSpawnRequest(Vec3.Zero));
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 1), "new participant scope can spawn");
            rig.Network.Leave(owner.Peer.Control); rig.Frames(3);
            rig.Clock.Advance(SpawnAuthorityRules.DepartureGraceMicros + 1); rig.Frames(3);
            Check(host.Agents.Values.All(agent => !agent.IsAlive), "departure grace cleans every canonical robot");

        }

        private static void QuotaIsAutomatic()
        {
            var (_, _, guest, owner) = Setup();
            for (var i = 0; i < SpawnAuthorityRules.MaximumPendingSpawns; i++)
                Check(Spawn(guest, owner, new RobotAgentSpawnRequest(Vec3.Zero)).Succeeded, "spawn within participant quota");
            var refused = Spawn(guest, owner, new RobotAgentSpawnRequest(Vec3.Zero));
            Check(!refused.Succeeded && refused.ErrorCode == ModErrorCode.RateLimited &&
                guest.Agents.Count == SpawnAuthorityRules.MaximumPendingSpawns, "quota refuses before another body is built");
            owner.Lifetime.Dispose();
            Check(guest.Agents.Values.All(agent => !agent.IsAlive), "pending robots end with their participant scope");
        }

        private static OperationResult<IRobotAgent> Spawn(FirstPartyRobots robots, AuthorityMod owner, RobotAgentSpawnRequest request)
        {
            var result = robots.Provider.Spawn(owner.Seam, request);
            // The owner facade tracks the returned handle on its exact consuming lifetime.
            if (result.TryGetValue(out var agent)) owner.Lifetime.Track(agent);
            return result;
        }

        private static void QueuedCommandsAreBoundedAndScoped()
        {
            var (rig, host, guest, owner) = Setup();
            var pending = Spawn(guest, owner, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            for (var i = 0; i < NetworkWorldClient.MaximumEntityCommands; i++)
                Check(pending.SetName("Pending").Succeeded, "bounded commands may wait for adoption");
            Check(pending.SetName("Overflow").ErrorCode == ModErrorCode.RateLimited, "queue overflow reports backpressure");
            owner.Lifetime.Dispose();
            Check(rig.Run(() => host.Agents.Count > 0 && host.Agents.Values.All(agent => !agent.IsAlive)),
                "stopped scope cancels pending commands and its body");
            var next = new AuthorityMod(owner.Peer, ModExecutionContext.Participant);
            var replacement = Spawn(guest, next, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            Check(replacement.SetName("New scope").Succeeded && rig.Run(() => host.Agents.Values.Any(agent => agent.IsAlive && agent.Name == "New scope")),
                "a new scope starts with room and does not inherit old commands");
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("First-party replication: " + message); }
    }
}
