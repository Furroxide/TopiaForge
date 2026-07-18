using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>
    /// Test-only seam compiled beside the Unity-free controller sources. None of these members ship in the mod.
    /// </summary>
    internal sealed partial class ZombiesController
    {
        internal ZombiesPhase TestingPhase => phase;
        internal int TestingWave => wave;
        internal int TestingPendingSpawns => pendingSpawns;
        internal int TestingScore => score;
        internal int TestingCredits => shop.Balance;
        internal int TestingUplinkCharges => uplinkCharges;
        internal float TestingIntegrity => integrity;
        internal bool TestingCharging => charging;
        internal IReadOnlyList<ZombieEnemy> TestingEnemies => enemies;

        internal bool TestingSpawn(ZombieKind kind, Vec3 position) => TrySpawnEnemy(position, kind);
        internal void TestingFireZapper(bool charged) => FireZapper(charged);
        internal void TestingDamagePlayer(float amount) => DamagePlayer(amount);
        internal void TestingSetPendingSpawns(int count) => pendingSpawns = count;
        internal void TestingSetUplinkCharges(int count) => uplinkCharges = Math.Max(0, count);
        internal void TestingApplyOverride(ZombieEnemy enemy, OverrideResolution resolution) =>
            ApplyOverride(enemy, resolution, standDown: false);
        internal void TestingEnterGameOver() => EnterGameOver();
        internal void TestingSetWavePhase()
        {
            phase = ZombiesPhase.Wave;
            pendingSpawns = 0;
        }
    }
}
