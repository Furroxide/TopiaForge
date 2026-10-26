using System;
using HarmonyLib;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Presence's engine side over the game (WS3.10): the local player through <c>PlayerController</c> and its
    /// <c>FirstPersonController</c>, avatars under one root that outlives scenes, the ground and the room to stand for the
    /// spawn ring, and the bounds of the loaded world. The player and its parts are found once and kept until they go, and
    /// health is read through field accessors made once, so a frame's sample allocates nothing. Main thread only.
    /// </summary>
    internal sealed class UnityPresenceBackend : IPresenceBackend
    {
        // How far below a slot's probe the ground may be: the probe starts 2 m above the spawn height, and a step of 1.5 m
        // below it is the most the ring takes.
        private const float GroundReach = 4f;

        // How far the world's bounds reach past what its colliders cover: room to jump and fall without being called a cheat.
        private const float BoundsMargin = 64f;
        private const float BoundsHeadroom = 256f;
        private const float BoundsRefreshSeconds = 1f;

        private static readonly AccessTools.FieldRef<Health, float>? HealthField = Accessor("health");
        private static readonly AccessTools.FieldRef<Health, float>? MaximumHealthField = Accessor("maxHealth");

        private readonly UnityEntityRegistry entities;
        private readonly RaycastHit[] hits = new RaycastHit[16];
        private readonly Collider[] overlaps = new Collider[16];
        private PlayerController? player;
        private Transform? playerTransform;
        private FirstPersonController? controller;
        private Transform? eyes;
        private Health? health;
        private bool healthResolved;
        private Grabbable? held;
        private string? heldId;
        private IEntity? playerEntity;
        private GameObject? root;
        private Transform? rootTransform;
        private bool boundsKnown;
        private PresenceBounds bounds = PresenceBounds.Everywhere;
        private int boundsScenes = -1;
        private float boundsCheckedAt = float.NegativeInfinity;

        internal UnityPresenceBackend(UnityEntityRegistry entities)
        {
            this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
        }

        public IEntity? LocalPlayerEntity
        {
            get
            {
                UnityMainThreadGuard.AssertCurrent();
                var current = Resolve();
                if (current == null) return null;
                if (playerEntity == null || !playerEntity.IsAlive) playerEntity = entities.GetOrCreate(current.gameObject);
                return playerEntity;
            }
        }

        public bool TryReadLocalPlayer(out PresenceLocalSample sample)
        {
            UnityMainThreadGuard.AssertCurrent();
            sample = default;
            var current = Resolve();
            if (current == null || playerTransform == null) return false;
            var feet = playerTransform.position;
            var view = current.GetCameraRotation();
            var eyeHeight = eyes != null ? eyes.position.y - feet.y : 1.6f;
            var crouching = controller != null && controller.IsCrouched;
            sample = new PresenceLocalSample(new NetVector3(feet.x, feet.y, feet.z), new NetQuaternion(view.x, view.y, view.z, view.w), eyeHeight, crouching);
            return true;
        }

        public bool TryReadLocalHealth(out float current, out float maximum)
        {
            UnityMainThreadGuard.AssertCurrent();
            current = 0f;
            maximum = 0f;
            var found = ResolveHealth();
            if (found == null || HealthField == null || MaximumHealthField == null) return false;
            current = HealthField(found);
            maximum = MaximumHealthField(found);
            return true;
        }

        public string? ReadLocalHeldItem()
        {
            UnityMainThreadGuard.AssertCurrent();
            var current = Resolve();
            var item = current != null ? current.HeldItem : null;

            // A held item the game destroyed reads as nothing held.
            if (item == null) item = null;
            if (ReferenceEquals(item, held)) return heldId;
            held = item;
            heldId = item != null ? OwnerItemService.GetItemId(item.gameObject) : null;
            return heldId;
        }

        public bool TeleportLocalPlayer(NetVector3 feet, float yawDegrees, bool keepFacing)
        {
            UnityMainThreadGuard.AssertCurrent();
            var current = Resolve();
            if (current == null || playerTransform == null || !feet.IsFinite) return false;
            var position = new Vector3(feet.X, feet.Y, feet.Z);
            Quaternion? facing = keepFacing ? (Quaternion?)null : Quaternion.Euler(0f, yawDegrees, 0f);
            if (controller != null)
            {
                controller.TeleportTo(position, facing);
                return true;
            }

            var character = playerTransform.GetComponent<CharacterController>();
            var wasEnabled = character != null && character.enabled;
            try
            {
                if (character != null) character.enabled = false;
                playerTransform.position = position;
                if (facing.HasValue) playerTransform.rotation = facing.Value;
            }
            finally
            {
                if (character != null) character.enabled = wasEnabled;
            }

            return true;
        }

        public IPresenceAvatarObject CreateAvatar(int index, PresenceTint tint)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (root == null || rootTransform == null)
            {
                root = new GameObject("TopiaForge Players");
                UnityEngine.Object.DontDestroyOnLoad(root);
                rootTransform = root.transform;
            }

            return new UnityPresenceAvatar(rootTransform, index, tint, entities);
        }

        public NetVector3? FindGround(NetVector3 from)
        {
            UnityMainThreadGuard.AssertCurrent();
            var count = Physics.RaycastNonAlloc(new Vector3(from.X, from.Y, from.Z), Vector3.down, hits, GroundReach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var nearest = float.PositiveInfinity;
            NetVector3? ground = null;
            for (var index = 0; index < count; index++)
            {
                var hit = hits[index];
                if (hit.collider == null || IsPlayers(hit.collider.transform) || hit.distance >= nearest) continue;
                nearest = hit.distance;
                ground = new NetVector3(hit.point.x, hit.point.y, hit.point.z);
            }

            return ground;
        }

        public bool IsClear(NetVector3 feet)
        {
            UnityMainThreadGuard.AssertCurrent();
            var radius = UnityPresenceAvatar.Radius;
            var bottom = new Vector3(feet.X, feet.Y + radius + 0.05f, feet.Z);
            var top = new Vector3(feet.X, feet.Y + UnityPresenceAvatar.Height - radius, feet.Z);

            // Anything solid in the way blocks a slot, another player included: two players are never placed inside each other.
            return Physics.OverlapCapsuleNonAlloc(bottom, top, radius, overlaps, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) == 0;
        }

        public bool TryGetWorldBounds(out PresenceBounds world)
        {
            UnityMainThreadGuard.AssertCurrent();
            var now = Time.unscaledTime;
            if (now - boundsCheckedAt >= BoundsRefreshSeconds)
            {
                boundsCheckedAt = now;
                var scenes = SceneKey();
                if (scenes != boundsScenes)
                {
                    boundsScenes = scenes;
                    MeasureBounds();
                }
            }

            world = bounds;
            return boundsKnown;
        }

        /// <summary>Finds the player again once the one held is gone, and the parts presence reads with it.</summary>
        private PlayerController? Resolve()
        {
            if (player != null && playerTransform != null) return player;
            player = PlayerController.FindPlayer();
            playerTransform = null;
            controller = null;
            eyes = null;
            health = null;
            healthResolved = false;
            held = null;
            heldId = null;
            playerEntity = null;
            if (player == null) return null;
            playerTransform = player.transform;
            controller = player.FPSController;
            var attach = controller != null ? controller.CameraAttachPoint : null;
            eyes = attach != null ? attach.transform : null;
            return player;
        }

        private Health? ResolveHealth()
        {
            var current = Resolve();
            if (current == null) return null;
            if (healthResolved && health != null) return health;
            healthResolved = true;
            health = current.GetComponent<Health>();
            if (health == null) health = current.GetComponentInChildren<Health>(true);
            if (health == null) health = current.GetComponentInParent<Health>();
            return health;
        }

        /// <summary>Whether a collider belongs to the local player or to an avatar, which the ground probe looks through.</summary>
        private bool IsPlayers(Transform part)
        {
            if (rootTransform != null && part.IsChildOf(rootTransform)) return true;
            return playerTransform != null && part.IsChildOf(playerTransform);
        }

        private static int SceneKey()
        {
            unchecked
            {
                var key = SceneManager.sceneCount;
                for (var index = 0; index < SceneManager.sceneCount; index++)
                {
                    var scene = SceneManager.GetSceneAt(index);
                    if (scene.isLoaded) key = key * 397 ^ scene.handle;
                }

                return key;
            }
        }

        /// <summary>The loaded scenes' solid colliders, widened by the margins: measured once each time the scenes change.</summary>
        private void MeasureBounds()
        {
            var colliders = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var found = false;
            var union = default(UnityEngine.Bounds);
            foreach (var collider in colliders)
            {
                if (collider == null || collider.isTrigger || IsPlayers(collider.transform)) continue;
                var box = collider.bounds;
                if (!IsFinite(box.min) || !IsFinite(box.max)) continue;
                if (!found)
                {
                    union = box;
                    found = true;
                }
                else
                {
                    union.Encapsulate(box);
                }
            }

            boundsKnown = found;
            bounds = found
                ? PresenceBounds.FromMeters(new NetVector3(union.min.x, union.min.y, union.min.z), new NetVector3(union.max.x, union.max.y, union.max.z), BoundsMargin, BoundsHeadroom)
                : PresenceBounds.Everywhere;
        }

        private static bool IsFinite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static AccessTools.FieldRef<Health, float>? Accessor(string field)
        {
            try
            {
                return AccessTools.FieldRefAccess<Health, float>(field);
            }
            catch (Exception)
            {
                // A game build without the field reads no health; presence sends none rather than failing.
                return null;
            }
        }
    }
}
