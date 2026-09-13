using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;
using UnityEngine;

namespace TopiaForge.SandboxAcceptance.Native
{
    internal sealed class NativeFixtureObjects : ICreatorContentFactory, IDisposable
    {
        private readonly List<GameObject> props = new List<GameObject>();
        private GameObject? borrowed;
        private readonly List<GameObject> retiredBorrowed = new List<GameObject>();
        internal int CleanupPendingObjects => props.Count(p => p != null) + retiredBorrowed.Count(p => p != null);
        private readonly IUnityInteropService interop;
        internal NativeFixtureObjects(IModContext context) { interop = context.RequireUnityInterop(); }
        internal GameObject? Borrowed => borrowed;
        internal string BorrowedRosterId => borrowed == null ? "" : "robot-native:robot-scene:" + borrowed.GetInstanceID();
        internal OperationResult<bool> PrepareBorrowed(IModContext context)
        {
            props.RemoveAll(p => p == null);
            retiredBorrowed.RemoveAll(p => p == null);
            if (borrowed != null) return OperationResult<bool>.Failure(ModErrorCode.Conflict, "A native fixture robot already exists.");
            if (!context.Extensions.TryGet<IRobotAgentService>(out var robots) || robots == null
                || !context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Native fixture robot/player unavailable.");
            var spawned = robots.Spawn(new RobotAgentSpawnRequest(player.Position + new Vec3(3f, 0f, 3f), brainMode: RobotBrainMode.Dormant,
                name: "Sandbox acceptance template"));
            if (!spawned.TryGetValue(out var agent)) return OperationResult<bool>.Failure(spawned.ErrorCode, spawned.ErrorMessage);
            try
            {
                // Exact trusted RobotKit native surface only; no caller-selected member or type.
                if (agent.GetType().FullName != "TopiaForge.RobotKit.RobotAgent")
                    return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Unexpected RobotKit implementation.");
                var property = agent.GetType().GetProperty("NativeGameObject", BindingFlags.Instance | BindingFlags.Public);
                if (!(property?.GetValue(agent) is GameObject template))
                    return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Verified RobotKit native object bridge unavailable.");
                // An inactive parent prevents clone OnEnable work before its native
                // LLM components and identity anchors are explicitly suppressed.
                var inactiveRoot = new GameObject("Sandbox acceptance inactive construction");
                inactiveRoot.SetActive(false);
                try
                {
                    borrowed = UnityEngine.Object.Instantiate(template, inactiveRoot.transform);
                    borrowed.SetActive(false);
                    borrowed.transform.SetParent(null, true);
                }
                finally { UnityEngine.Object.DestroyImmediate(inactiveRoot); }
                borrowed.name = "Sandbox acceptance borrowed robot";
                foreach (var component in borrowed.GetComponentsInChildren<Component>(true))
                {
                    if (component != null && component.GetType().FullName == "TopiaForge.RobotKit.RobotAgentEntityIdentityAnchor")
                        UnityEngine.Object.DestroyImmediate(component); // Only the fresh inactive test-owned clone.
                }
                var snapshot = agent.GetType().GetField("nativeBrainSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(agent);
                foreach (var component in borrowed.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component.GetType().Name != "LLMAgent") continue;
                    // The authored clone never runs an LLM loop. Seed only native enum state from the
                    // actual pre-dormant snapshot so the real Dormant UI edit has something to restore.
                    if (component is Behaviour behaviour) behaviour.enabled = false;
                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var disabled = component.GetType().GetField("llmDisabled", flags);
                    if (disabled?.FieldType != typeof(bool)) throw new InvalidOperationException("Native LLM suppression is unavailable.");
                    disabled.SetValue(component, true);
                    foreach (var pair in new[] { ("initialState", "InitialState"), ("state", "State") })
                    {
                        var field = component.GetType().GetField(pair.Item1, flags);
                        var original = snapshot?.GetType().GetField(pair.Item2)?.GetValue(snapshot);
                        if (field != null && original != null && field.FieldType.IsEnum && field.FieldType == original.GetType()) field.SetValue(component, original);
                    }
                }
                borrowed.SetActive(true);
                return OperationResult<bool>.Success(true);
            }
            catch (Exception exception)
            {
                if (borrowed != null) UnityEngine.Object.Destroy(borrowed);
                borrowed = null;
                return OperationResult<bool>.Failure(ModErrorCode.External, "Native borrowed fixture: " + exception.Message);
            }
            finally { try { agent.Despawn(); } finally { agent.Dispose(); } }
        }
        public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
        {
            if (props.Count >= 1024) return OperationResult<ICreatorSourceInstance>.Failure(ModErrorCode.RateLimited, "Native fixture cap reached.");
            var prop = GameObject.CreatePrimitive(PrimitiveType.Cube);
            prop.name = "Sandbox acceptance prop";
            Apply(prop.transform, transform);
            var wrapped = interop.Wrap(prop);
            if (!wrapped.TryGetValue(out var entity)) { UnityEngine.Object.Destroy(prop); return OperationResult<ICreatorSourceInstance>.Failure(wrapped.ErrorCode, wrapped.ErrorMessage); }
            props.Add(prop);
            return OperationResult<ICreatorSourceInstance>.Success(new Prop(prop, entity));
        }
        internal object[] ObserveProps() => props.Where(p => p != null).Select(p => (object)new Dictionary<string, object?>
        { ["instanceId"] = p.GetInstanceID(), ["sceneHandle"] = p.scene.handle, ["active"] = p.activeInHierarchy,
            ["transform"] = Transform(p.transform) }).ToArray();
        internal static float[] Transform(UnityEngine.Transform t) => new[] { t.position.x, t.position.y, t.position.z,
            t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w, t.localScale.x, t.localScale.y, t.localScale.z };
        private static void Apply(UnityEngine.Transform target, TransformState value)
        {
            target.SetPositionAndRotation(new Vector3(value.Position.X, value.Position.Y, value.Position.Z),
                new Quaternion(value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W));
            target.localScale = new Vector3(value.Scale.X, value.Scale.Y, value.Scale.Z);
        }
        public void Dispose()
        {
            foreach (var prop in props) if (prop != null) UnityEngine.Object.Destroy(prop);
            // Retain references until Unity has really destroyed the objects; observations must cross a frame barrier.
            if (borrowed != null) { retiredBorrowed.Add(borrowed); UnityEngine.Object.Destroy(borrowed); }
            borrowed = null;
        }
        private sealed class Prop : ICreatorSourceInstance, IEntity
        {
            private readonly GameObject value;
            private bool disposed;
            public Prop(GameObject value, IEntity entity) { this.value = value; Entity = entity; Id = entity.Id; }
            public IEntity Entity { get; }
            public string Id { get; }
            public string Name => value != null ? value.name : "";
            public bool IsAlive => !disposed && value != null;
            public Vec3 Position => IsAlive ? new Vec3(value.transform.position.x, value.transform.position.y, value.transform.position.z) : Vec3.Zero;
            public bool TryGetTransform(out TransformState transform)
            {
                if (!IsAlive) { transform = TransformState.Identity; return false; }
                var t = value.transform;
                transform = new TransformState(Position, new Quat(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w),
                    new Vec3(t.localScale.x, t.localScale.y, t.localScale.z));
                return true;
            }
            public OperationResult<TransformState> SetTransform(TransformState transform)
            {
                if (!IsAlive) return OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "Fixture prop is destroyed.");
                Apply(value.transform, transform);
                return OperationResult<TransformState>.Success(transform);
            }
            public void Dispose() { if (disposed) return; disposed = true; if (value != null) UnityEngine.Object.Destroy(value); }
        }
    }
}
