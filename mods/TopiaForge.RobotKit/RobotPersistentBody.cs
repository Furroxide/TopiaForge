using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotAgentService
    {
        private readonly string bodyPrefix = "robotkit:body-" + Guid.NewGuid().ToString("N") + "-";
        private long bodySerial;
        private int bodyVertices;

        internal OperationResult<IRobotBody> CreatePersistentBody(RobotBodyRequest request)
        {
            if (disposed) return OperationResult<IRobotBody>.Failure(ModErrorCode.Unavailable, "RobotKit has stopped.");
            var prefab = ResolveCachedPrefab();
            if (prefab == null) return OperationResult<IRobotBody>.Failure(ModErrorCode.Unavailable, "No robot is loaded to compose a body.");
            EnsureRoots();
            GameObject? clone = null;
            GameObject? anchor = null;
            var meshes = new List<Mesh>();
            try
            {
                RobotBodyVisual.CheckHierarchy(prefab.transform);
                // The incubator is inactive. Native scripts never receive Awake/OnEnable on this clone.
                clone = UnityEngine.Object.Instantiate(prefab, incubator!.transform, false);
                clone.SetActive(false);
                GameReflection.ConfigureBrain(clone, RobotBrainMode.Dormant, logger);
                Quieten(clone);
                RobotAvatarAppearance.Apply(clone, request.Appearance);
                anchor = new GameObject("Powered-down robot");
                anchor.SetActive(false);
                anchor.transform.SetParent(root!.transform, false);
                var p = request.Position; var q = request.Rotation;
                anchor.transform.position = new Vector3(p.X, p.Y, p.Z);
                anchor.transform.rotation = new Quaternion(q.X, q.Y, q.Z, q.W);
                var vertices = RobotBodyVisual.Copy(clone, anchor, meshes, Math.Min(250000, 4000000 - bodyVertices));
                var id = bodyPrefix + (++bodySerial).ToString(System.Globalization.CultureInfo.InvariantCulture);
                anchor.AddComponent<RobotAgentEntityIdentityAnchor>().Initialize(id);
                // Only the visual snapshot is activated: no native scripts, animator, audio, rigidbody or collider.
                anchor.SetActive(true);
                bodyVertices += vertices;
                var body = new RobotPersistentBody(id, anchor, request, meshes, () => bodyVertices -= vertices);
                anchor = null;
                return OperationResult<IRobotBody>.Success(body);
            }
            catch (Exception)
            {
                if (anchor != null) { anchor.SetActive(false); UnityEngine.Object.Destroy(anchor); }
                foreach (var mesh in meshes) if (mesh != null) UnityEngine.Object.Destroy(mesh);
                return OperationResult<IRobotBody>.Failure(ModErrorCode.Unavailable, "The loaded robot cannot provide the requested cosmetic body.");
            }
            finally { if (clone != null) UnityEngine.Object.Destroy(clone); }
        }
    }

    // This is a render snapshot, not a native agent or corpse. Even unrecognized native behaviours stay inactive.
    internal static class RobotBodyVisual
    {
        internal static void CheckHierarchy(Transform root)
        {
            var count = 0;
            Check(root, 0, ref count);
        }

        private static void Check(Transform node, int depth, ref int count)
        {
            if (++count > 1024 || depth > 64 || node.childCount > 1024)
                throw new InvalidOperationException("The robot visual hierarchy exceeds its limit.");
            for (var i = 0; i < node.childCount; i++) Check(node.GetChild(i), depth + 1, ref count);
        }

        internal static int Copy(GameObject clone, GameObject anchor, List<Mesh> meshes, int vertexBudget)
        {
            CheckHierarchy(clone.transform);
            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0 || renderers.Length > 256) throw new InvalidOperationException("Invalid body renderer count.");
            var transforms = new Dictionary<Transform, Transform>();
            var visual = CopyTransforms(clone.transform, anchor.transform, transforms);
            visual.localPosition = new Vector3(0, 0.35f, 0);
            visual.localRotation = Quaternion.Euler(90, 0, 0);
            var block = new MaterialPropertyBlock();
            var vertices = 0;
            var visible = 0;
            foreach (var source in renderers)
            {
                if (!source.enabled || !VisibleBelow(source.transform, clone.transform)) continue;
                Mesh? mesh;
                if (source is SkinnedMeshRenderer skin)
                {
                    if (skin.sharedMesh == null) throw new InvalidOperationException("A body skin has no mesh.");
                    vertices = checked(vertices + skin.sharedMesh.vertexCount);
                    if (vertices > vertexBudget) throw new InvalidOperationException("The body mesh budget was exceeded.");
                    mesh = new Mesh(); meshes.Add(mesh);
                    skin.BakeMesh(mesh);
                    if (mesh.vertexCount == 0) throw new InvalidOperationException("The body skin could not be baked.");
                }
                else if (source is MeshRenderer)
                {
                    mesh = source.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh == null) throw new InvalidOperationException("A body renderer has no mesh.");
                    vertices = checked(vertices + mesh.vertexCount);
                    if (vertices > vertexBudget) throw new InvalidOperationException("The body mesh budget was exceeded.");
                }
                else throw new InvalidOperationException("Unsupported body renderer.");
                var materials = source.sharedMaterials;
                if (materials.Length == 0 || materials.Length > 32) throw new InvalidOperationException("Invalid body materials.");
                foreach (var material in materials) if (material == null) throw new InvalidOperationException("A body material is unavailable.");
                var target = transforms[source.transform].gameObject;
                target.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = target.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = materials;
                renderer.shadowCastingMode = source.shadowCastingMode;
                renderer.receiveShadows = source.receiveShadows;
                source.GetPropertyBlock(block); renderer.SetPropertyBlock(block);
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    block.Clear(); source.GetPropertyBlock(block, slot); renderer.SetPropertyBlock(block, slot);
                }
                visible++;
            }
            if (visible == 0 || vertices == 0) throw new InvalidOperationException("The body has no visible meshes.");
            return vertices;
        }

        private static bool VisibleBelow(Transform node, Transform root)
        {
            for (var current = node; current != root; current = current.parent)
                if (!current.gameObject.activeSelf) return false;
            return true;
        }

        private static Transform CopyTransforms(Transform source, Transform parent, Dictionary<Transform, Transform> map)
        {
            var target = new GameObject("Body visual").transform;
            target.SetParent(parent, false);
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;
            map.Add(source, target);
            for (var i = 0; i < source.childCount; i++) CopyTransforms(source.GetChild(i), target, map);
            return target;
        }
    }

    internal sealed class RobotPersistentBody : IRobotBody, IInternalNativeObjectEntity
    {
        private GameObject? anchor;
        private readonly List<Mesh> meshes;
        private readonly RobotBodyRequest request;
        private Action? releaseBudget;
        internal RobotPersistentBody(string id, GameObject anchor, RobotBodyRequest request, List<Mesh> meshes, Action releaseBudget)
        { Id = id; this.anchor = anchor; this.request = request; this.meshes = meshes; this.releaseBudget = releaseBudget; }
        public string Id { get; }
        public string Name => "Powered-down robot";
        public bool IsAlive => anchor != null;
        public Vec3 Position => request.Position;
        public Quat Rotation => request.Rotation;
        public AvatarAppearance Appearance => request.Appearance;
        public object? NativeObject => anchor;
        public void Dispose()
        {
            var ended = anchor; anchor = null;
            if (ended != null) { ended.SetActive(false); UnityEngine.Object.Destroy(ended); }
            foreach (var mesh in meshes) if (mesh != null) UnityEngine.Object.Destroy(mesh);
            meshes.Clear();
            var release = releaseBudget; releaseBudget = null; release?.Invoke();
        }
    }
}
