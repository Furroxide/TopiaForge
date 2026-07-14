using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TopiaForge.GravityGun
{
    internal sealed class GravityGunController
    {
        private readonly GravityGunConfig config;
        private readonly IModLogger logger;
        private readonly GravityGunEffects effects;
        private readonly GravityGunModel model;
        private Camera? activeCamera;
        private IGravityGunTarget? held;
        private float holdDistance;
        private bool disposed;

        public GravityGunController(GravityGunConfig config, IModLogger logger)
        {
            this.config = config;
            this.logger = logger;
            effects = new GravityGunEffects();
            model = new GravityGunModel();
            holdDistance = config.DefaultHoldDistance;
            RefreshCamera();
        }

        public void Update(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            RefreshCameraIfNeeded();
            if (activeCamera == null)
            {
                ReleaseHeld(false);
                effects.EndHold();
                model.Update(null, false, false, deltaTime);
                return;
            }

            var inputBlocked = InputBlocked();
            model.Update(activeCamera, !inputBlocked, held != null, deltaTime);
            if (inputBlocked)
            {
                ReleaseHeld(false);
                effects.EndHold();
                return;
            }

            if (held == null)
            {
                effects.EndHold();
                if (Input.GetMouseButtonDown(1))
                {
                    TryAcquire(activeCamera);
                }

                return;
            }

            if (!held.IsAlive)
            {
                ReleaseHeld(false);
                return;
            }

            UpdateHoldDistance();

            if (Input.GetMouseButtonDown(0))
            {
                ThrowHeld(activeCamera);
                return;
            }

            if (Input.GetMouseButtonUp(1) || !Input.GetMouseButton(1))
            {
                ReleaseHeld(false);
                return;
            }

            held.UpdateHold(activeCamera, holdDistance, config, deltaTime);
            effects.UpdateHold(activeCamera, held.Position, config.ParticleIntensity, deltaTime);
        }

        public void OnSceneLoaded(string sceneName)
        {
            ReleaseHeld(false);
            activeCamera = null;
            RefreshCamera();
            logger.Debug("Gravity Gun scene refresh: " + sceneName);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ReleaseHeld(false);
            effects.Dispose();
            model.Dispose();
        }

        private void TryAcquire(Camera camera)
        {
            var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, config.MaxRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                return;
            }

            if (!TryCreateTarget(hit, out var target))
            {
                return;
            }

            held = target;
            holdDistance = config.DefaultHoldDistance;
            effects.Burst(camera, hit.point, new Color(0.35f, 0.95f, 1f, 0.95f), 24);
            logger.Debug("Gravity Gun acquired target: " + target.Name);
        }

        private void ThrowHeld(Camera camera)
        {
            if (held == null)
            {
                return;
            }

            var position = held.Position;
            held.Throw(camera.transform.forward, config);
            held = null;
            effects.EndHold();
            effects.Burst(camera, position, new Color(1f, 0.55f, 0.16f, 0.95f), 34);
        }

        private void ReleaseHeld(bool quiet)
        {
            if (held == null)
            {
                return;
            }

            var camera = activeCamera;
            var position = held.Position;
            held.Release();
            held = null;
            effects.EndHold();

            if (!quiet && camera != null)
            {
                effects.Burst(camera, position, new Color(0.42f, 0.85f, 1f, 0.75f), 14);
            }
        }

        private void UpdateHoldDistance()
        {
            var wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) <= 0.001f)
            {
                return;
            }

            holdDistance = Mathf.Clamp(
                holdDistance + wheel * config.ScrollStep,
                config.MinHoldDistance,
                config.MaxHoldDistance);
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
            for (var i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i].isActiveAndEnabled)
                {
                    activeCamera = cameras[i];
                    return;
                }
            }

            activeCamera = null;
        }

        private bool InputBlocked()
        {
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                return false;
            }

            if (config.RequireCursorLocked)
            {
                return true;
            }

            var current = EventSystem.current;
            return current != null && current.IsPointerOverGameObject();
        }

        private bool TryCreateTarget(RaycastHit hit, out IGravityGunTarget target)
        {
            if (RobotGrabSupport.TryCreateTarget(hit, config, logger, out var robotTarget) && robotTarget != null)
            {
                target = robotTarget;
                return true;
            }

            var body = FindEligibleBody(hit);
            if (body != null)
            {
                target = HeldRigidbody.Capture(body, config);
                return true;
            }

            target = null!;
            return false;
        }

        private static Rigidbody? FindEligibleBody(RaycastHit hit)
        {
            var body = hit.rigidbody;
            if (IsEligible(body))
            {
                return body;
            }

            var collider = hit.collider;
            if (collider == null)
            {
                return null;
            }

            body = collider.GetComponentInParent<Rigidbody>();
            return IsEligible(body) ? body : null;
        }

        private static bool IsEligible(Rigidbody? body)
        {
            return body != null && body.gameObject != null && !body.isKinematic;
        }
    }
}
