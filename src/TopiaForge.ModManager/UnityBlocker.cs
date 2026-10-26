using System;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>A collider-only runtime entity, shared by canonical spawns and the built-in replica factory.</summary>
    internal sealed class UnityBlocker : IBlocker, IUnityOwnedEntity
    {
        private GameObject? instance;
        private readonly IEntity entity;
        private readonly Transform rootTransform;

        private UnityBlocker(GameObject instance, IEntity entity, BlockerRequest request)
        {
            this.instance = instance;
            this.entity = entity;
            rootTransform = instance.transform;
            InitialTransform = request.Placement;
            Size = request.Size;
        }

        internal static OperationResult<IBlocker> Create(UnityEntityRegistry entities, BlockerRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            UnityMainThreadGuard.AssertCurrent();
            var root = new GameObject("TopiaForge.Blocker");
            try
            {
                root.layer = request.BlocksSight ? 0 : 2; // Default / Ignore Raycast: Unity's built-in layers.
                root.transform.position = UnityPhysicsBackend.ToUnity(request.Placement.Position);
                var rotation = request.Placement.Rotation;
                root.transform.rotation = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
                var box = root.AddComponent<BoxCollider>();
                box.size = UnityPhysicsBackend.ToUnity(request.Size);
                var body = root.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                var blocker = new UnityBlocker(root, entities.GetOrCreate(root), request);
                UnityBlockerPreview.Track(root.transform, request.Size);
                return OperationResult<IBlocker>.Success(blocker);
            }
            catch
            {
                UnityEngine.Object.Destroy(root);
                throw;
            }
        }

        public string Id => entity.Id;
        public string Name => entity.Name;
        public bool IsAlive => instance != null && entity.IsAlive;
        public Vec3 Position => entity.Position;
        public TransformState InitialTransform { get; }
        public Vec3 Size { get; }
        public IEntity InnerEntity => entity;

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            var current = instance;
            instance = null;
            UnityBlockerPreview.Untrack(rootTransform);
            if (current == null) return;
            // Destroy is deferred by Unity; collision stops immediately when the handle is disposed.
            current.SetActive(false);
            UnityEngine.Object.Destroy(current);
        }
    }
}
