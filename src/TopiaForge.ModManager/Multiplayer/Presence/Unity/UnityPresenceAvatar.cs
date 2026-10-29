using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// One remote player's avatar in the game (WS3.10): a root under presence's own root, which outlives scenes, with a
    /// kinematic rigidbody and a capsule collider a player's height, so players bump into each other and physics and
    /// robots find it; an identity anchor, so every part of it names one entity; and a capsule in the player's colour,
    /// which hides while the avatar wears a provider's body. Main thread only; moving it allocates nothing.
    /// </summary>
    internal sealed class UnityPresenceAvatar : IPresenceAvatarObject, IPresenceAvatarPresentation
    {
        /// <summary>A standing player's height and radius, about the game's own player's.</summary>
        internal const float Height = 1.8f;

        internal const float Radius = 0.35f;

        private const float CrouchedHeight = 1.2f;

        // A nametag floats this far above the head.
        private const float NametagClearance = 0.45f;

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int Color = Shader.PropertyToID("_Color");

        private readonly UnityEntityRegistry entities;
        private readonly GameObject root;
        private readonly Transform rootTransform;
        private readonly CapsuleCollider collider;
        private readonly GameObject capsule;
        private readonly Transform capsuleTransform;
        private Material? capsuleMaterial;
        private GameObject? body;
        private IEntity? entity;
        private bool crouching;
        private bool poweredDown;
        private bool disposed;

        internal UnityPresenceAvatar(Transform container, int index, PresenceTint tint, UnityEntityRegistry entities)
        {
            this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
            root = new GameObject("TopiaForge Player " + index);
            rootTransform = root.transform;
            rootTransform.SetParent(container, false);
            var rigidbody = root.AddComponent<Rigidbody>();
            rigidbody.isKinematic = true;
            rigidbody.useGravity = false;
            rigidbody.interpolation = RigidbodyInterpolation.None;
            collider = root.AddComponent<CapsuleCollider>();
            collider.radius = Radius;
            collider.direction = 1;
            root.AddComponent<PresenceAvatarIdentity>().Initialize("avatar-" + index);
            capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "Capsule";
            var primitiveCollider = capsule.GetComponent<Collider>();
            if (primitiveCollider != null) UnityEngine.Object.DestroyImmediate(primitiveCollider);
            capsuleTransform = capsule.transform;
            capsuleTransform.SetParent(rootTransform, false);
            Shape(Height);
            Tint(capsule, tint);
        }

        public IEntity Entity
        {
            get
            {
                UnityMainThreadGuard.AssertCurrent();
                if (entity == null || !entity.IsAlive) entity = entities.GetOrCreate(root);
                return entity;
            }
        }

        public bool IsAlive => !disposed && root != null;

        public bool IsWearingBody => body != null;

        public NetVector3 HeadPosition
        {
            get
            {
                var top = rootTransform.position + Vector3.up * ((poweredDown ? 0.5f : crouching ? CrouchedHeight : Height) + NametagClearance);
                return new NetVector3(top.x, top.y, top.z);
            }
        }

        public void Place(NetVector3 feet, float yawDegrees)
        {
            if (disposed || root == null) return;
            rootTransform.SetPositionAndRotation(new Vector3(feet.X, feet.Y, feet.Z), Quaternion.Euler(0f, yawDegrees, 0f));
        }

        public bool Wear(IInternalAvatarBody worn)
        {
            if (worn == null) throw new ArgumentNullException(nameof(worn));
            if (disposed || root == null || body != null) return false;
            var made = (worn.Root as IInternalNativeObjectEntity)?.NativeObject as GameObject;
            if (made == null) return false;
            var part = made.transform;
            part.SetParent(rootTransform, false);
            part.localPosition = Vector3.zero;
            part.localRotation = Quaternion.identity;
            capsule.SetActive(false);
            body = made;
            return true;
        }

        public void TakeOffBody()
        {
            body = null;
            if (!disposed && capsule != null) capsule.SetActive(true);
        }

        public void Animate(float speedMetersPerSecond, bool crouched)
        {
            if (disposed || root == null || crouched == crouching) return;
            crouching = crouched;
            Shape(crouched ? CrouchedHeight : Height);
        }

        public void Present(AvatarAppearance appearance, bool poweredDown)
        {
            this.poweredDown = poweredDown;
            var colour = appearance.Plastic;
            var renderer = capsule.GetComponent<Renderer>();
            if (renderer != null)
            {
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                var tint = new UnityEngine.Color(colour.Red, colour.Green, colour.Blue, 1f);
                block.SetColor(BaseColor, tint); block.SetColor(Color, tint); renderer.SetPropertyBlock(block);
            }
            capsuleTransform.localRotation = poweredDown ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
            Shape(crouching ? CrouchedHeight : Height);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (root != null) UnityEngine.Object.Destroy(root);
            if (capsuleMaterial != null) UnityEngine.Object.Destroy(capsuleMaterial);
            capsuleMaterial = null;
        }

        private void Shape(float height)
        {
            collider.height = height;
            collider.center = new Vector3(0f, height / 2f, 0f);

            // Unity's capsule is two metres tall and one across.
            capsuleTransform.localPosition = new Vector3(0f, poweredDown ? Radius : height / 2f, 0f);
            capsuleTransform.localScale = new Vector3(Radius * 2f, height / 2f, Radius * 2f);
        }

        private void Tint(GameObject shape, PresenceTint tint)
        {
            var renderer = shape.GetComponent<Renderer>();
            if (renderer == null) return;
            // CreatePrimitive supplies a built-in-pipeline material. Colour overrides cannot make its
            // shader render in HDRP. Use the same opaque shader as generated Worlds geometry, and own
            // this material explicitly: destroying the GameObject does not destroy runtime materials.
            var shader = Shader.Find("HDRP/Lit");
            if (shader != null && shader.isSupported)
            {
                capsuleMaterial = new Material(shader)
                {
                    name = "TopiaForge Player Capsule",
                    hideFlags = HideFlags.HideAndDontSave
                };
                renderer.sharedMaterial = capsuleMaterial;
            }
            else
            {
                // Keep the capsule visible so an unavailable shader is observable, never a hidden peer.
                Debug.LogError("TopiaForge: the player capsule requires a supported HDRP/Lit shader.");
            }

            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            var colour = new UnityEngine.Color(tint.Red, tint.Green, tint.Blue, 1f);
            block.SetColor(BaseColor, colour);
            block.SetColor(Color, colour);
            renderer.SetPropertyBlock(block);
        }
    }

    /// <summary>Gives every part of an avatar one entity identity, as RobotKit's robots have, so a physics hit on any part names the avatar.</summary>
    internal sealed class PresenceAvatarIdentity : MonoBehaviour, IRuntimeEntityIdentityAnchor
    {
        private string runtimeEntityId = string.Empty;

        public string RuntimeEntityId => runtimeEntityId;

        internal void Initialize(string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityId)) throw new ArgumentException("An avatar needs an entity id.", nameof(entityId));
            runtimeEntityId = entityId;
        }
    }
}
