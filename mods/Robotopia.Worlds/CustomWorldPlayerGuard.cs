using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.Worlds
{
    /// <summary>
    /// Kill plane for custom worlds: mod-authored geometry can have gaps the generated arena never has, so
    /// when the player falls below the threshold it is put back at the world's spawn point instead of
    /// falling forever. Lives on the arena root (this mod's assembly — never inside content bundles) and
    /// dies with it on session end.
    /// </summary>
    internal sealed class CustomWorldPlayerGuard : MonoBehaviour
    {
        private const float CheckIntervalSeconds = 0.5f;
        private const float RespawnCooldownSeconds = 2f;

        private GameLevelBridge? bridge;
        private Vector3 respawnPosition;
        private float killY;
        private IModLogger? logger;
        private float nextCheckTime;
        private float cooldownUntil;

        public void Initialize(GameLevelBridge bridge, Vector3 respawnPosition, float killY, IModLogger logger)
        {
            this.bridge = bridge;
            this.respawnPosition = respawnPosition;
            this.killY = killY;
            this.logger = logger;
        }

        private void Update()
        {
            if (bridge == null || Time.unscaledTime < nextCheckTime)
            {
                return;
            }

            nextCheckTime = Time.unscaledTime + CheckIntervalSeconds;
            if (Time.unscaledTime < cooldownUntil)
            {
                return;
            }

            var player = bridge.GetPlayerTransform();
            if (player == null || player.position.y >= killY)
            {
                return;
            }

            // The cooldown covers the frames it takes a physics/character controller to register the move,
            // so one fall never triggers a burst of repositions.
            cooldownUntil = Time.unscaledTime + RespawnCooldownSeconds;
            if (bridge.RepositionPlayer(respawnPosition + Vector3.up * 1.5f))
            {
                logger?.Info("Custom world kill plane: player respawned at the spawn point.");
            }
        }
    }
}
