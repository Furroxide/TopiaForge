using System;
using System.Threading;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Worlds
{
    internal sealed class OpenSandboxKillPlane : MonoBehaviour
    {
        private WorldReadiness? readiness;
        private Transform? expectedPlayer;
        private CancellationToken stopping;
        private IModLogger? logger;
        private float nextCheck;
        private float nextRespawn;
        private const float Depth = 50f;

        public void Initialize(WorldReadiness ready, CancellationToken stoppingToken, IModLogger ownerLogger)
        {
            expectedPlayer = NativeWorldPlayer.GetTransform();
            if (expectedPlayer == null) throw new InvalidOperationException("The prepared Open Sandbox player disappeared.");
            readiness = ready; stopping = stoppingToken; logger = ownerLogger;
        }

        private void Update()
        {
            if (readiness == null || stopping.IsCancellationRequested || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            if (Time.unscaledTime < nextRespawn) return;
            try
            {
                if (!UnityWorldScene.ContainsRoot(readiness.Scene, gameObject)) return;
                var player = NativeWorldPlayer.GetTransform();
                if (player == null || expectedPlayer == null || player != expectedPlayer || stopping.IsCancellationRequested
                    || player.position.y >= readiness.Spawn.Position.Y - Depth) return;
                nextRespawn = Time.unscaledTime + 2f;
                var spawn = readiness.Spawn;
                NativeWorldPlayer.Place(player, new Vector3(spawn.Position.X, spawn.Position.Y, spawn.Position.Z),
                    new Quaternion(spawn.Rotation.X, spawn.Rotation.Y, spawn.Rotation.Z, spawn.Rotation.W));
            }
            catch (Exception error)
            {
                try { logger?.Warn("Open Sandbox kill-plane placement failed: " + error.Message); }
                catch { /* A logging fault must not escape the native update callback. */ }
            }
        }
    }
}
