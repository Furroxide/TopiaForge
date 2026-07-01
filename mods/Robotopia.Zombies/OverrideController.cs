using System;
using Robotopia.Mods;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Robotopia.Zombies
{
    // The robot-AI input layer (a sibling of ZapperController on the session root). It paints the infected robot under
    // the crosshair with the same screen-center raycast the zapper uses, and owns the "uplink" charge economy. Two
    // verbs sit on it:
    //   JACK IN (default E) — open a free-form LLM conversation with the painted robot (ZombiesController freezes the
    //                         horde and runs the talk). Spends one uplink charge; the deep, true-to-base-game verb.
    //   BROADCAST (Q)       — a deterministic, offline-safe crowd "stand down" pulse to every overridable robot in
    //                         radius (the panic/no-backend valve). Spends charges + a cooldown.
    // It does not decide outcomes; it acquires the target and hands off. All HUD state (charges, cooldown, whether you
    // are aiming a jackable robot) is exposed for the controller to forward to the HUD.
    internal sealed class OverrideController : MonoBehaviour
    {
        private ZombiesController? controller;
        private ZombiesConfig? config;
        private IModLogger? logger;
        private Camera? activeCamera;

        private KeyCode jackInKey = KeyCode.E;
        private KeyCode broadcastKey = KeyCode.Q;

        private int charges;
        private float regenTimer;
        private float broadcastCooldown;
        private bool aimingHijackable;

        public void Initialize(ZombiesController controller, ZombiesConfig config, IModLogger logger)
        {
            this.controller = controller;
            this.config = config;
            this.logger = logger;
            jackInKey = ParseKey(config.JackInKey, KeyCode.E);
            broadcastKey = ParseKey(config.BroadcastKey, KeyCode.Q);
            RefreshCamera();
            ResetState();
        }

        // --- HUD-facing state (read by the controller, forwarded to the HUD) ----------------------------------
        public bool Enabled => config != null && (config.OverrideEnabled || config.ConversationEnabled);
        public int Charges => charges;
        public int MaxCharges => config?.OverrideCharges ?? 0;
        public bool AimingHijackable => aimingHijackable;

        // Kept so the HUD/controller pass-throughs still compile; the single-target command radial was replaced by
        // the conversation verb, and the broadcast uses a fixed deterministic stand-down.
        public OverrideCommand Selected => OverrideCommand.StandDown;

        // 0..1 progress toward the next charge (1 when full).
        public float RegenFraction
        {
            get
            {
                if (config == null || charges >= config.OverrideCharges || config.OverrideChargeRegenSeconds <= 0f)
                {
                    return 1f;
                }

                return Mathf.Clamp01(regenTimer / config.OverrideChargeRegenSeconds);
            }
        }

        // 0..1 broadcast readiness (1 when ready to fire).
        public float BroadcastReadyFraction
        {
            get
            {
                if (config == null || config.BroadcastCooldownSeconds <= 0f)
                {
                    return 1f;
                }

                return Mathf.Clamp01(1f - (broadcastCooldown / config.BroadcastCooldownSeconds));
            }
        }

        // Reset on (re)start so a new run begins with a full battery and no leftover cooldown.
        public void ResetState()
        {
            charges = config?.OverrideCharges ?? 0;
            regenTimer = 0f;
            broadcastCooldown = 0f;
            aimingHijackable = false;
        }

        private void Update()
        {
            if (controller == null || config == null || !controller.IsActive || !Enabled)
            {
                aimingHijackable = false;
                return;
            }

            if (controller.GameOver)
            {
                aimingHijackable = false;
                return;
            }

            RegenCharges(Time.deltaTime);
            broadcastCooldown = Mathf.Max(0f, broadcastCooldown - Time.deltaTime);

            // A held conversation owns input; don't read verbs or pay for a raycast while frozen.
            if (controller.Conversing || InputBlocked())
            {
                aimingHijackable = false;
                return;
            }

            // Paint the robot under the crosshair for the JACK-IN aim reticle (one raycast per active frame).
            aimingHijackable = config.ConversationEnabled && TryResolveTarget(out _);

            if (config.ConversationEnabled && Input.GetKeyDown(jackInKey))
            {
                TryJackIn();
            }

            if (config.OverrideEnabled && Input.GetKeyDown(broadcastKey))
            {
                TryBroadcast();
            }
        }

        private void RegenCharges(float deltaTime)
        {
            if (config == null || charges >= config.OverrideCharges)
            {
                regenTimer = 0f;
                return;
            }

            regenTimer += deltaTime;
            if (regenTimer >= config.OverrideChargeRegenSeconds)
            {
                regenTimer = 0f;
                charges = Mathf.Min(config.OverrideCharges, charges + 1);
            }
        }

        // Open a conversation channel with the painted robot. The uplink charge is spent only when the channel
        // actually opens (the controller validates availability/target).
        private void TryJackIn()
        {
            if (controller == null || config == null)
            {
                return;
            }

            if (!controller.ConversationAvailable)
            {
                controller.ShowOverrideHint("JACK-IN: channel offline");
                return;
            }

            if (charges <= 0)
            {
                controller.ShowOverrideHint("JACK-IN: no uplink charge");
                return;
            }

            if (!TryResolveTarget(out var target) || target == null)
            {
                controller.ShowOverrideHint("JACK-IN: no target");
                return;
            }

            if (controller.BeginConversation(target))
            {
                charges--;
            }
        }

        private void TryBroadcast()
        {
            if (controller == null || config == null)
            {
                return;
            }

            if (broadcastCooldown > 0f)
            {
                controller.ShowOverrideHint("BROADCAST: recharging");
                return;
            }

            if (charges < config.BroadcastChargeCost)
            {
                controller.ShowOverrideHint("BROADCAST: need " + config.BroadcastChargeCost + " charges");
                return;
            }

            charges -= config.BroadcastChargeCost;
            broadcastCooldown = config.BroadcastCooldownSeconds;
            controller.BroadcastCommand(OverrideCommand.StandDown, config.BroadcastRadius);
        }

        // Screen-center raycast (identical formula to ZapperController.FirePrimary) → the overridable robot under the
        // crosshair, if any.
        private bool TryResolveTarget(out ZombieEnemyController? target)
        {
            target = null;
            RefreshCameraIfNeeded();
            if (activeCamera == null || config == null)
            {
                return false;
            }

            var ray = activeCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(ray, out var hit, config.ZapperRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                var enemy = hit.collider.GetComponentInParent<ZombieEnemyController>();
                if (enemy != null && enemy.IsOverridable)
                {
                    target = enemy;
                    return true;
                }
            }

            return false;
        }

        private static bool InputBlocked()
        {
            // Locked FPS play should not raycast UI. JACK-IN/game-over unlock the cursor explicitly, which is the
            // modal gate; avoiding EventSystem raycasts here keeps retained HUD graphics out of the hot path.
            return Cursor.lockState != CursorLockMode.Locked;
        }

        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (!string.IsNullOrWhiteSpace(name) && Enum.TryParse<KeyCode>(name.Trim(), true, out var parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private void RefreshCameraIfNeeded()
        {
            if (activeCamera == null || !activeCamera.isActiveAndEnabled)
            {
                RefreshCamera();
            }
        }

        private void RefreshCamera()
        {
            activeCamera = Camera.main;
            if (activeCamera != null && activeCamera.isActiveAndEnabled)
            {
                return;
            }

            var cameras = Camera.allCameras;
            for (var index = 0; index < cameras.Length; index++)
            {
                if (cameras[index] != null && cameras[index].isActiveAndEnabled)
                {
                    activeCamera = cameras[index];
                    return;
                }
            }

            activeCamera = null;
        }
    }
}
