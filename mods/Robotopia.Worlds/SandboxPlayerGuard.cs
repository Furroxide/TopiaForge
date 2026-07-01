using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.Worlds
{
    /// <summary>
    /// Safety net for the Open Sandbox arena. The game's own <c>UgcImportPlayBootstrap</c> normally spawns the
    /// player into the sandbox scene; this guard waits a short grace period (so that bootstrap's own spawn can
    /// register first) and, only if no player exists by then, spawns one from the bootstrap's player prefab.
    /// The grace plus the up-front presence check avoid a duplicate player (the game rejects a second one).
    /// </summary>
    internal sealed class SandboxPlayerGuard : MonoBehaviour
    {
        private GameLevelBridge? bridge;
        private GameObject? playerPrefab;
        private IModLogger? logger;
        private Vector3 spawnPosition;
        private float graceRemaining;
        private bool resolved;

        public void Initialize(GameLevelBridge bridge, GameObject? playerPrefab, Vector3 spawnPosition, IModLogger logger, float graceSeconds)
        {
            this.bridge = bridge;
            this.playerPrefab = playerPrefab;
            this.spawnPosition = spawnPosition;
            this.logger = logger;
            graceRemaining = graceSeconds;
        }

        private void Update()
        {
            if (resolved || bridge == null)
            {
                return;
            }

            // Give the scene's native bootstrap time to spawn the player before we consider stepping in.
            graceRemaining -= Time.deltaTime;
            if (graceRemaining > 0f)
            {
                return;
            }

            resolved = true;
            if (bridge.IsPlayerPresent())
            {
                // The sandbox scene's bootstrap spawned the player as expected; nothing to do.
                return;
            }

            if (playerPrefab == null)
            {
                logger?.Warn("Worlds sandbox: no player was spawned and no player prefab is available to spawn one.");
                return;
            }

            bridge.SpawnPlayer(playerPrefab, spawnPosition);
            logger?.Info("Worlds sandbox: the play scene did not spawn a player; spawned a fallback player into the arena.");
        }
    }
}
