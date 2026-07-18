using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        // Exercise deterministic gameplay rules through SDK fakes instead of reaching into Unity internals.
        private static void CompleteWaveExceedsTheConcurrentAliveCap()
        {
            var config = FastConfig();
            config.BaseZombiesPerWave = 3;
            config.MaxAliveZombies = 1;
            config.ScorePerKill = 10;
            config.ComboKillsPerTier = 100;
            using var harness = new Harness(config);
            harness.ReadyToWave();

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var maximumConcurrent = 0;
            for (var kill = 0; kill < 3; kill++)
            {
                PumpUntil(
                    harness,
                    () => harness.Robots.Agents.ActiveAgents.Count == 1,
                    ref maximumConcurrent,
                    "the next capped wave enemy should spawn");
                var agent = (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0];
                seenIds.Add(agent.Id);
                AimAtProxy(harness, agent);

                harness.Context.Input.SetValue("zapper-fire", 1f);
                harness.Advance(0.01f);
                maximumConcurrent = Math.Max(maximumConcurrent, harness.Robots.Agents.ActiveAgents.Count);
                Assert(!agent.IsAlive, "a lethal zapper hit through a physics proxy should defeat its owned robot");
                harness.Context.Input.SetValue("zapper-fire", 0f);
                harness.Advance(0.01f);
                maximumConcurrent = Math.Max(maximumConcurrent, harness.Robots.Agents.ActiveAgents.Count);
            }

            PumpUntil(
                harness,
                () => harness.Controller.TestingPhase == ZombiesPhase.InterWave,
                ref maximumConcurrent,
                "the wave should clear after every planned enemy is defeated");
            Assert(seenIds.Count == 3
                && maximumConcurrent == 1
                && harness.Controller.TestingPendingSpawns == 0
                && harness.Controller.TestingScore == 30,
                "wave size is a total spawn budget, not a value clamped down to the alive cap");
        }

        private static void BruteHealthIsControllerOwned()
        {
            var config = FastConfig();
            config.ZombieHealth = 10f;
            config.BruteHealthMult = 3f;
            config.ZapperDamage = 10f;
            config.BruteScore = 77;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Brute, new Vec3(0f, 0f, 5f)),
                "a deterministic Brute should spawn for the health regression");

            var agent = (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0];
            AimAtProxy(harness, agent);
            harness.Controller.TestingFireZapper(charged: false);
            Assert(agent.IsAlive && harness.Controller.TestingEnemies[0].Health == 20f,
                "one base-damage shot must not kill a triple-health Brute");
            harness.Controller.TestingFireZapper(charged: false);
            Assert(agent.IsAlive && harness.Controller.TestingEnemies[0].Health == 10f,
                "the Brute remains alive until custom archetype health is exhausted");
            Assert(agent.DamageTaken == 0f,
                "Zombies custom health must not also call native ApplyDamage and create a second health authority");

            harness.Controller.TestingFireZapper(charged: false);
            Assert(!agent.IsAlive
                && harness.Controller.TestingScore == 77
                && agent.DamageTaken == 0f,
                "the final custom-health hit kills and scores exactly once");
        }

        private static void ControlAndWorldClocksStaySeparated()
        {
            var config = FastConfig();
            config.StartingCountdownSeconds = 2f;
            using var harness = new Harness(config, withChronos: true);
            var slow = harness.Chronos!.Slow("zombies-clock-test", 0.1f);
            Assert(slow.Succeeded, "the Chronos test slow should be available");

            harness.Advance(1f);
            harness.Advance(1f);
            harness.Advance(1f);
            Assert(harness.Controller.TestingWave == 1
                && harness.Controller.TestingPhase == ZombiesPhase.Wave,
                "starting countdowns use the unscaled control clock under slow motion");

            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 4f)),
                "a frozen clock-test enemy should spawn");
            var enemy = harness.Controller.TestingEnemies[0];
            enemy.Freeze(1f, standDown: false);

            harness.Advance(2f);
            Assert(enemy.State == HijackState.Frozen,
                "world-owned enemy effects advance by scaled world time, not two seconds of control time");
            for (var index = 0; index < 7; index++)
            {
                harness.Advance(1f);
            }

            Assert(enemy.State == HijackState.Frozen,
                "a one-second freeze remains active before one scaled world second has elapsed");
            harness.Advance(1f);
            harness.Advance(1f);
            Assert(enemy.State == HijackState.Hostile,
                "the enemy effect expires after its scaled-world duration");
        }

        private static void HardFreezeBlocksCustomSimulation()
        {
            var config = FastConfig();
            config.ZombieAttackDamage = 25f;
            using var harness = new Harness(config, withChronos: true);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            harness.Controller.TestingSetPendingSpawns(1);
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 1f)),
                "a close hostile should spawn for the hard-freeze regression");
            var beforeIntegrity = harness.Controller.TestingIntegrity;
            var beforeAgents = harness.Robots.Agents.ActiveAgents.Count;
            var freeze = harness.Chronos!.Freeze("external-freeze", suspendPlayer: true).Value!;

            harness.Advance(1f);
            Assert(harness.Controller.TestingIntegrity == beforeIntegrity
                && harness.Robots.Agents.ActiveAgents.Count == beforeAgents,
                "zero world delta must block both custom attacks and spawn progression");

            freeze.Dispose();
            harness.Advance(0.1f);
            Assert(harness.Controller.TestingIntegrity < beforeIntegrity,
                "custom enemy simulation resumes after the composed freeze releases");
        }

        private static void StrandedRangeGapCannotDeadlockWave()
        {
            var config = FastConfig();
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(
                    ZombieKind.Grunt,
                    new Vec3(0f, 0f, config.ZombieAttackRange + 0.5f)),
                "a hostile should spawn inside the former stranded-tracking gap");

            for (var second = 0; second < 14; second++)
            {
                harness.Advance(1f);
            }

            Assert(harness.Controller.TestingPhase == ZombiesPhase.InterWave,
                "a nonmoving hostile just outside attack range is recovered instead of deadlocking the wave");
        }

        private static void RestartRestoresNativeHealthAndCleansAgents()
        {
            var config = FastConfig();
            config.PlayerIntegrity = 100f;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 4f)),
                "a restart-cleanup enemy should spawn");

            harness.Controller.TestingDamagePlayer(40f);
            Assert(harness.Context.Player.Health!.Current == 60f
                && harness.Controller.TestingIntegrity == 60f,
                "Zombies damage keeps native health and gamemode integrity synchronized");
            Assert(harness.Controller.Restart().Succeeded,
                "an active Zombies run should restart");
            Assert(harness.Context.Player.Health!.Current == 100f
                && harness.Controller.TestingIntegrity == 100f
                && harness.Robots.Agents.ActiveAgents.Count == 0
                && harness.Controller.TestingPhase == ZombiesPhase.WaitingForWorld,
                "restart restores captured native health and releases every session-owned robot");
        }

        private static void RestartRestoresPartiallyInjuredNativeHealthExactly()
        {
            var config = FastConfig();
            config.PlayerIntegrity = 100f;
            using var harness = new Harness(config);
            harness.Context.Player.Health = new PlayerHealthSnapshot(60f, 100f);
            harness.Advance(0.01f);
            Assert(harness.Context.Player.Heal(25f, "temporary field repair").Succeeded,
                "the partial-health fixture should receive temporary run healing");

            Assert(harness.Controller.Restart().Succeeded
                && harness.Context.Player.Health!.Current == 60f,
                "restart removes temporary run healing and restores the exact captured native health");
        }

        private static void DelayedNativeHealthCaptureStillRestores()
        {
            var config = FastConfig();
            config.PlayerIntegrity = 100f;
            using var harness = new Harness(config);
            harness.Context.Player.Health = null;
            harness.Advance(0.01f);
            harness.Context.Player.Health = new PlayerHealthSnapshot(80f, 100f);
            harness.Controller.TestingDamagePlayer(20f);

            Assert(harness.Context.Player.Health.Current == 60f
                && harness.Controller.Restart().Succeeded
                && harness.Context.Player.Health.Current == 80f,
                "the first later health snapshot becomes the cleanup baseline before Zombies mirrors damage");
        }

        private static void SameIdPlayerReplacementDoesNotReceiveOldNativeHealth()
        {
            var config = FastConfig();
            config.PlayerIntegrity = 100f;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            var original = (FakeEntity)harness.Robots.Agents.PlayerEntity!;
            harness.Controller.TestingDamagePlayer(40f);
            Assert(harness.Context.Player.Health!.Current == 60f,
                "the original player fixture should receive Zombies native-health mirroring");

            original.Destroy();
            harness.Robots.Agents.PlayerEntity = new FakeEntity(original.Id, "Replacement Player", Vec3.Zero);
            harness.Context.Player.Health = new PlayerHealthSnapshot(25f, 100f);

            Assert(harness.Controller.Restart().Succeeded
                && harness.Context.Player.Health.Current == 25f,
                "cleanup must compare player identity by reference, not reuse a stale same-ID health baseline");
        }



        private static void RuntimeMathSaturatesAtNumericBoundaries()
        {
            Assert(ZombiesRuntimeMath.WaveSize(int.MaxValue, int.MaxValue, int.MaxValue) == int.MaxValue,
                "wave size saturates instead of overflowing");
            Assert(ZombiesRuntimeMath.ComboMultiplier(int.MaxValue, 1, 100) == 100,
                "combo multiplication clamps before narrowing to int");
            Assert(ZombiesRuntimeMath.SaturatingMultiply(int.MaxValue, 2) == int.MaxValue
                && ZombiesRuntimeMath.ScoreCredits(int.MaxValue, float.MaxValue) == int.MaxValue,
                "score and credit arithmetic saturate at the public integer boundary");
        }
    }
}
