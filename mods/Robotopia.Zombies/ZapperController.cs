using Robotopia.Mods;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Robotopia.Zombies
{
    internal sealed class ZapperController : MonoBehaviour
    {
        private const float MuzzleOffset = 0.35f;
        private const int BeamSegments = 5;

        private ZombiesController? controller;
        private ZombiesConfig? config;
        private IModLogger? logger;
        private Camera? activeCamera;
        private float cooldown;
        private float chargeCooldown;
        private float chargeTimer;
        private bool charging;
        private Material? beamMaterial;
        private GameObject? beamObject;
        private LineRenderer? beamLine;
        private float beamHideTime;

        public float ReadyFraction
        {
            get
            {
                var cooldownSeconds = CooldownSeconds;
                if (config == null || cooldownSeconds <= 0f)
                {
                    return 1f;
                }

                return Mathf.Clamp01(1f - (cooldown / cooldownSeconds));
            }
        }

        // Shop upgrades scale the zapper live (per-run multipliers, config untouched): RAPID COILS
        // shortens the cooldown, ZAPPER GAIN boosts primary and charged damage alike.
        private float CooldownSeconds => (config?.ZapperCooldownSeconds ?? 0f) * (controller?.Upgrades.ZapperCooldownMult ?? 1f);
        private float PrimaryDamage => (config?.ZapperDamage ?? 0f) * (controller?.Upgrades.ZapperDamageMult ?? 1f);
        private float ChargedDamage => (config?.ChargeShotDamage ?? 0f) * (controller?.Upgrades.ZapperDamageMult ?? 1f);

        public void Initialize(ZombiesController controller, ZombiesConfig config, IModLogger logger)
        {
            this.controller = controller;
            this.config = config;
            this.logger = logger;
            RefreshCamera();
            EnsureBeam();
        }

        private void Update()
        {
            // The zapper is holstered during a JACK-IN freeze (talking is a commitment — you can't also shoot),
            // while the requisitions window is up, and at game-over. The unlocked cursor would already gate
            // firing, but holster explicitly so a held charge doesn't survive the interruption.
            if (controller == null || config == null || !controller.IsActive || controller.GameOver || controller.Conversing || controller.Shopping)
            {
                CancelCharge();
                return;
            }

            cooldown = Mathf.Max(0f, cooldown - Time.deltaTime);
            chargeCooldown = Mathf.Max(0f, chargeCooldown - Time.deltaTime);
            UpdateBeamVisibility();

            if (InputBlocked())
            {
                CancelCharge();
                return;
            }

            UpdateCharge();

            // Primary tap fires while the charged alt-fire is not mid-charge, so the two modes don't fight over one
            // trigger pull.
            if (!charging && Input.GetMouseButton(0) && cooldown <= 0f)
            {
                FirePrimary();
            }
        }

        // Do not discharge into the world while a menu/pause screen is up or the pointer is over UI. FPS
        // controllers lock the cursor during play and release it for menus, so the lock state is the gate.
        private static bool InputBlocked()
        {
            // In locked first-person play the pointer is virtual and should never raycast against HUD graphics.
            // Querying EventSystem here forces every active GraphicRaycaster to scan UI each frame, which is costly
            // now that the Zombies HUD is retained uGUI.
            return Cursor.lockState != CursorLockMode.Locked;
        }

        // Right mouse charges a piercing shot; releasing at full charge fires it. The charge fraction drives the
        // crosshair charge meter via the controller.
        private void UpdateCharge()
        {
            if (config == null)
            {
                return;
            }

            if (!config.ChargeShotEnabled)
            {
                controller?.SetChargeFraction(0f);
                return;
            }

            if (Input.GetMouseButton(1) && chargeCooldown <= 0f)
            {
                charging = true;
                chargeTimer += Time.deltaTime;
            }
            else if (charging)
            {
                // Released (or cooldown began): fire if it reached full charge, then reset.
                if (chargeTimer >= config.ChargeShotSeconds)
                {
                    FireCharged();
                }

                CancelCharge();
            }

            var fraction = config.ChargeShotSeconds > 0f ? Mathf.Clamp01(chargeTimer / config.ChargeShotSeconds) : 0f;
            controller?.SetChargeFraction(charging ? fraction : 0f);
        }

        private void CancelCharge()
        {
            charging = false;
            chargeTimer = 0f;
            controller?.SetChargeFraction(0f);
        }

        private void FirePrimary()
        {
            if (config == null)
            {
                return;
            }

            RefreshCameraIfNeeded();
            if (activeCamera == null)
            {
                logger?.Warn("Zombies zapper could not find an active camera.");
                return;
            }

            cooldown = CooldownSeconds;
            var cameraTransform = activeCamera.transform;
            var ray = activeCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            var cameraPosition = cameraTransform.position;
            var end = cameraPosition + (ray.direction * config.ZapperRange);

            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, config.ZapperRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                end = hit.point;
                var enemy = hit.collider.GetComponentInParent<ZombieEnemyController>();
                if (enemy != null)
                {
                    enemy.TakeDamage(PrimaryDamage, hit.point, ray.direction, false);
                }
                else if (hit.rigidbody != null && !hit.rigidbody.isKinematic && !IsGameRobot(hit.collider))
                {
                    // Only nudge loose physics props. Spawned zombies are kinematic (handled above) and
                    // unrelated game robots should not be flung around by the zapper.
                    hit.rigidbody.AddForceAtPosition(
                        ray.direction * config.ZapperImpactForce,
                        hit.point,
                        ForceMode.Impulse);
                }
            }

            DrawBeamTo(cameraPosition, ray.direction, end, false);
        }

        // The charged alt-fire: one shot down the camera ray that pierces every zombie on the line (the swarm
        // answer) and is stopped only by solid world geometry. Each zombie takes the full charge damage.
        private void FireCharged()
        {
            if (config == null)
            {
                return;
            }

            RefreshCameraIfNeeded();
            if (activeCamera == null)
            {
                logger?.Warn("Zombies zapper could not find an active camera.");
                return;
            }

            chargeCooldown = config.ChargeShotCooldownSeconds;
            cooldown = Mathf.Max(cooldown, CooldownSeconds);

            var cameraTransform = activeCamera.transform;
            var ray = activeCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            var cameraPosition = cameraTransform.position;
            var end = cameraPosition + (ray.direction * config.ZapperRange);

            var hits = Physics.RaycastAll(ray, config.ZapperRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (var index = 0; index < hits.Length; index++)
            {
                var current = hits[index];
                var enemy = current.collider.GetComponentInParent<ZombieEnemyController>();
                if (enemy != null)
                {
                    enemy.TakeDamage(ChargedDamage, current.point, ray.direction, true);
                    if (!config.ChargeShotPierces)
                    {
                        end = current.point;
                        break;
                    }
                }
                else if (!IsGameRobot(current.collider))
                {
                    // Solid world geometry stops the beam (and unrelated robots simply don't block or take damage).
                    end = current.point;
                    break;
                }
            }

            DrawBeamTo(cameraPosition, ray.direction, end, true);
        }

        private static bool IsGameRobot(Collider collider)
        {
            return ReflectionUtil.IsGameRobotInParent(collider);
        }

        // Draw a jagged lightning beam from the muzzle to the hit point: interior points are jittered perpendicular
        // to the beam each shot so it reads as electricity rather than a laser.
        private void DrawBeamTo(Vector3 cameraPosition, Vector3 direction, Vector3 end, bool charged)
        {
            if (config == null)
            {
                return;
            }

            EnsureBeam();
            if (beamLine == null || beamObject == null)
            {
                return;
            }

            // Keep the visible muzzle from overshooting the hit when something is closer than the offset.
            var projected = Mathf.Min(MuzzleOffset, Vector3.Dot(end - cameraPosition, direction));
            var start = cameraPosition + (direction * projected);

            var widthScale = charged ? 1.3f : 1f;
            beamLine.startWidth = config.BeamWidthStart * widthScale;
            beamLine.endWidth = config.BeamWidthEnd * widthScale;
            if (charged)
            {
                beamLine.startColor = new Color(0.75f, 0.5f, 1f, 1f);
                beamLine.endColor = new Color(0.5f, 0.25f, 1f, 0f);
            }
            else
            {
                beamLine.startColor = new Color(0.7f, 0.95f, 1f, 1f);
                beamLine.endColor = new Color(0.3f, 0.6f, 1f, 0f);
            }

            var beamVector = end - start;
            var beamDir = beamVector.sqrMagnitude > 0.0001f ? beamVector.normalized : direction;
            var perpendicular = Vector3.Cross(beamDir, Vector3.up);
            if (perpendicular.sqrMagnitude < 0.0001f)
            {
                perpendicular = Vector3.Cross(beamDir, Vector3.forward);
            }

            perpendicular.Normalize();
            var perpendicular2 = Vector3.Cross(beamDir, perpendicular);
            var amplitude = config.BeamJitterAmplitude * (charged ? 1.4f : 1f);

            beamLine.positionCount = BeamSegments;
            for (var index = 0; index < BeamSegments; index++)
            {
                var t = (float)index / (BeamSegments - 1);
                var point = Vector3.Lerp(start, end, t);
                if (index > 0 && index < BeamSegments - 1)
                {
                    point += (perpendicular * Random.Range(-amplitude, amplitude)) +
                             (perpendicular2 * Random.Range(-amplitude, amplitude));
                }

                beamLine.SetPosition(index, point);
            }

            beamObject.SetActive(true);
            beamHideTime = Time.time + config.ZapperBeamLifetimeSeconds;
        }

        private void UpdateBeamVisibility()
        {
            if (beamObject != null && beamObject.activeSelf && Time.time >= beamHideTime)
            {
                beamObject.SetActive(false);
            }
        }

        private void EnsureBeam()
        {
            if (beamObject != null)
            {
                return;
            }

            beamObject = new GameObject("Zombies Zapper Beam");
            beamObject.transform.SetParent(transform, false);
            beamLine = beamObject.AddComponent<LineRenderer>();
            beamLine.positionCount = BeamSegments;
            beamLine.useWorldSpace = true;
            beamLine.numCapVertices = 2;
            beamLine.startWidth = config?.BeamWidthStart ?? 0.1f;
            beamLine.endWidth = config?.BeamWidthEnd ?? 0.03f;
            beamLine.startColor = new Color(0.7f, 0.95f, 1f, 1f);
            beamLine.endColor = new Color(0.3f, 0.6f, 1f, 0f);
            beamLine.sharedMaterial = GetBeamMaterial();
            beamObject.SetActive(false);
        }

        private Material GetBeamMaterial()
        {
            if (beamMaterial != null)
            {
                return beamMaterial;
            }

            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
            beamMaterial = new Material(shader);
            return beamMaterial;
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

        private void OnDestroy()
        {
            if (beamMaterial != null)
            {
                Destroy(beamMaterial);
                beamMaterial = null;
            }
        }
    }
}
