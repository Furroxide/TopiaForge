using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class FirstPartyLocalRobotTests
    {
        private static int assertions;

        internal static void Run()
        {
            assertions = 0;
            var failures = 0;
            foreach (var profile in new[] { ModNetworkClass.Standalone, ModNetworkClass.ClientLocal })
            {
                try { LocalCommands(profile); }
                catch (Exception exception)
                {
                    failures++;
                    Console.WriteLine(profile + ": " + exception.Message);
                }
            }
            if (failures != 0) throw new InvalidOperationException("Local robot profiles failed: " + failures);
            Console.WriteLine("First-party local robots: 2 groups passed, " + assertions + " assertions.");
        }

        private static void LocalCommands(ModNetworkClass profile)
        {
            var rig = new WorldRig();
            rig.UseScene(scene => scene.AddScene("Yard"));
            var robots = new FirstPartyRobots(rig.Host);
            var owner = new AuthorityMod(rig.Host, networkClass: profile);
            var robot = Spawn(robots, owner);
            var body = robots.Agents[robot.Id];
            Check(rig.Host.Runtime.Spawns.Find(body) == null, "local robots deliberately have no spawn ledger record");
            Check(robot.SetName("Local helper").Succeeded && body.Name == "Local helper", "SetName applies offline");
            Check(robot.SetBrainMode(RobotBrainMode.Autonomous).Succeeded && body.BrainMode == RobotBrainMode.Autonomous,
                "SetBrainMode applies offline");
            Check(robot.SetScale(2).Succeeded && body.Inner.Scale == 2, "SetScale applies offline");
            Check(robot.SetEmote("wave").Succeeded, "SetEmote applies offline");
            Check(robot.MoveTo(new Vec3(2, 0, 0)).Succeeded && robot.Stop().Succeeded, "direct movement remains usable");

            var rename = RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Name); RobotReplicationWire.Text(w, "Intruder", 128); });
            var sibling = new AuthorityMod(rig.Host, networkClass: profile);
            var foreign = new AuthorityMod(rig.Host, modId: "io.example.other", networkClass: profile);
            var shared = new AuthorityMod(rig.Host);
            foreach (var intruder in new[] { sibling, foreign, shared })
                Check(intruder.Seam.SubmitEntityCommand(body, RobotReplicationWire.Kind, rename).ErrorCode == ModErrorCode.NotAuthoritative,
                    "only the exact spawning context may command a local robot");
            Check(owner.Seam.SubmitEntityCommand(new Impostor(body), RobotReplicationWire.Kind, rename).ErrorCode == ModErrorCode.NotAuthoritative,
                "a matching process-local id does not confer ownership");
            Check(owner.Seam.SubmitEntityCommand(body, "other.kind", rename).ErrorCode == ModErrorCode.NotAuthoritative,
                "the local command kind must match the spawn");
            var malformed = RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Name); RobotReplicationWire.Text(w, "Malformed", 128); w.Write((byte)99); });
            Check(owner.Seam.SubmitEntityCommand(body, RobotReplicationWire.Kind, malformed).ErrorCode == ModErrorCode.InvalidArgument && body.Name == "Local helper",
                "local commands decode completely before effects");
            Check(robot.SetScale(float.NaN).ErrorCode == ModErrorCode.InvalidArgument && body.Inner.Scale == 2,
                "local commands retain numeric bounds");
            Check(owner.Seam.SubmitEntityCommand(body, RobotReplicationWire.Kind, new byte[16385]).ErrorCode == ModErrorCode.InvalidArgument,
                "local commands retain the runtime payload cap");
            var sharedRobot = Spawn(robots, shared);
            var record = rig.Host.Runtime.Spawns.Find(robots.Agents[sharedRobot.Id]);
            Check(record != null, "shared robots still use the ledger");
            rig.Host.Runtime.Spawns.Remove(record!);
            Check(sharedRobot.SetName("Missing record").ErrorCode == ModErrorCode.NotAuthoritative,
                "shared robots cannot fall back when their ledger record is gone");
            shared.Lifetime.Dispose();
            Check(robot.Despawn().Succeeded && !robot.IsAlive, "Despawn removes the local body");
            Check(!robot.SetName("Stale").Succeeded && !robot.SetBrainMode(RobotBrainMode.Dormant).Succeeded,
                "a despawned handle cannot mutate a retired body");

            var disposed = Spawn(robots, owner);
            disposed.Dispose();
            Check(!disposed.IsAlive && !disposed.SetName("Disposed").Succeeded, "handle disposal retires local commands");
            var scoped = Spawn(robots, owner);
            var survivor = Spawn(robots, sibling);
            owner.Lifetime.Dispose();
            Check(!scoped.IsAlive && survivor.IsAlive, "consuming lifetime cleanup leaves another context's robot alive");
            Check(scoped.SetName("Stopped").ErrorCode == ModErrorCode.Cancelled, "a stopping scope cannot command");
            Check(robots.Provider.Spawn(owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero)).ErrorCode == ModErrorCode.Cancelled,
                "a stopping scope cannot spawn");
            Check(survivor.SetName("Survivor").Succeeded, "another context keeps its command path");
            Check(rig.Host.Runtime.Spawns.Live().Count == 0, "local commands never create promotion records");
            sibling.Lifetime.Dispose();
            Check(!survivor.IsAlive, "the other lifetime cleans its own robot");
            if (profile == ModNetworkClass.ClientLocal)
            {
                var local = new AuthorityMod(rig.Host, networkClass: profile);
                var localRobot = Spawn(robots, local);
                rig.OpenToMultiplayer();
                var guest = rig.AddPlayer("guest");
                var replicas = new FirstPartyRobots(guest);
                rig.JoinAndSynchronize(guest);
                Check(localRobot.SetName("Host local").Succeeded && localRobot.IsAlive,
                    "client-local robots remain local on a listen host");
                Check(replicas.Agents.Count == 0 && rig.Host.Runtime.Spawns.Live().Count == 0,
                    "client-local robots never enter a shared baseline");
                local.Lifetime.Dispose();
                Check(!localRobot.IsAlive, "host-local cleanup still follows its consuming lifetime");
            }
        }

        private static IRobotAgent Spawn(FirstPartyRobots robots, AuthorityMod owner)
        {
            var result = robots.Provider.Spawn(owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero));
            Check(result.Succeeded, "offline spawn succeeds");
            var robot = result.Value!;
            owner.Lifetime.Track(robot);
            return robot;
        }

        private static void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Impostor : IEntity
        {
            private readonly IEntity body;
            internal Impostor(IEntity body) { this.body = body; }
            public string Id => body.Id;
            public string Name => body.Name;
            public bool IsAlive => body.IsAlive;
            public Vec3 Position => body.Position;
        }
    }
}
