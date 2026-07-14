using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Sandbox
{
    /// <summary>
    /// Places catalog items in the world: a crosshair raycast picks the spot (fixed distance ahead when
    /// nothing is hit), and every spawned prop is prepared as a plain physics object — collider plus a
    /// non-kinematic Rigidbody — so the Gravity Gun can grab it out of the box.
    /// </summary>
    internal sealed class PropSpawner
    {
        private const float FallbackDistance = 4f;
        private const float MinMass = 1f;
        private const float MaxMass = 50f;

        private readonly PropCatalog catalog;
        private readonly SandboxConfig config;
        private readonly IModLogger logger;

        public PropSpawner(PropCatalog catalog, SandboxConfig config, IModLogger logger)
        {
            this.catalog = catalog;
            this.config = config;
            this.logger = logger;
        }

        /// <summary>Spawns a catalog item where the camera looks. Null when the item cannot be created.</summary>
        public GameObject? Spawn(SandboxPropDefinition definition, Camera camera)
        {
            if (!catalog.TryInstantiate(definition, out var instance))
            {
                return null;
            }

            instance.name = "Sandbox Prop: " + definition.Id;
            PreparePhysics(instance);
            Place(instance, camera);
            return instance;
        }

        /// <summary>Where a robot should spawn: the crosshair ground point, or a spot ahead of the camera.</summary>
        public Vector3 ResolveSpawnPoint(Camera camera)
        {
            var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(ray, out var hit, config.SpawnDistanceMax, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                return hit.point;
            }

            var fallback = camera.transform.position + camera.transform.forward * FallbackDistance;
            // Without a hit the ray may be pointing at the sky; probe down so the point still lands on ground.
            return Physics.Raycast(fallback + Vector3.up, Vector3.down, out var ground, 30f)
                ? ground.point
                : fallback;
        }

        private void Place(GameObject instance, Camera camera)
        {
            var bounds = ComputeBounds(instance);
            var halfHeight = Mathf.Max(0.1f, bounds.extents.y);
            var target = ResolveSpawnPoint(camera) + Vector3.up * (halfHeight + 0.05f);

            // The pivot is not necessarily the bounds centre (UGC prefabs anchor arbitrarily); offset so the
            // *bounds* sit on the target point rather than the pivot.
            var pivotToCenter = bounds.center - instance.transform.position;
            instance.transform.position = target - new Vector3(pivotToCenter.x, 0f, pivotToCenter.z);
            instance.transform.rotation = Quaternion.Euler(0f, camera.transform.eulerAngles.y, 0f);
        }

        private void PreparePhysics(GameObject instance)
        {
            var bounds = ComputeBounds(instance);

            // A prop with no collider anywhere gets one box that matches what you see.
            if (instance.GetComponentInChildren<Collider>() == null)
            {
                var box = instance.AddComponent<BoxCollider>();
                box.center = instance.transform.InverseTransformPoint(bounds.center);
                var size = instance.transform.InverseTransformVector(bounds.size);
                box.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            }

            var body = instance.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = instance.AddComponent<Rigidbody>();
            }

            body.isKinematic = false;
            var volume = Mathf.Max(0.001f, bounds.size.x * bounds.size.y * bounds.size.z);
            body.mass = Mathf.Clamp(volume * 2f, MinMass, MaxMass);
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        private Bounds ComputeBounds(GameObject instance)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(instance.transform.position, Vector3.one);
            }

            var bounds = renderers[0].bounds;
            for (var index = 1; index < renderers.Length; index++)
            {
                bounds.Encapsulate(renderers[index].bounds);
            }

            // Some UGC prefabs carry oversized particle/effect renderers; a degenerate or huge bounds would
            // place the prop absurdly. Clamp to something sane and let physics settle the rest.
            if (bounds.size.magnitude > 60f)
            {
                logger.Debug("Sandbox clamped oversized bounds for '" + instance.name + "'.");
                bounds = new Bounds(bounds.center, Vector3.ClampMagnitude(bounds.size, 60f));
            }

            return bounds;
        }
    }
}
