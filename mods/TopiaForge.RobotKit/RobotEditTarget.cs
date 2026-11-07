using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal sealed class RobotEditTarget : IRobotEditTarget
    {
        internal const string ManagedRobotMessage = "RobotKit-managed robots cannot be edited by the scene editor.";
        private readonly GameObject root;

        public RobotEditTarget(GameObject root, Component agent)
        {
            this.root = root;
            Agent = agent;
            InstanceId = root.GetInstanceID();
            SceneHandle = root.scene.handle;
            Id = "robot-scene:" + InstanceId;
        }

        public string Id { get; }
        public string DisplayName => root != null ? root.name : string.Empty;
        public string SceneName => root != null && root.scene.IsValid() ? root.scene.name : string.Empty;
        public bool IsAlive => root != null && Agent != null && root.scene.IsValid() && root.scene.handle == SceneHandle;
        public bool IsNativeSceneObject => true;
        public int InstanceId { get; }
        public int SceneHandle { get; }
        internal GameObject Root => root;
        internal Component Agent { get; }
        internal bool IsManaged => HasManagedRobot(root, Agent);

        internal static bool HasManagedRobot(GameObject root, Component agent)
        {
            // The spawn anchor can be above a composed RobotBody. Brain and transform edits also
            // reach below the selected root, so check its subtree without scanning adjacent actors.
            return root.GetComponentInParent<RobotAgentEntityIdentityAnchor>(true) != null
                || root.GetComponentInChildren<RobotAgentEntityIdentityAnchor>(true) != null
                || agent.GetComponentInParent<RobotAgentEntityIdentityAnchor>(true) != null;
        }

        public bool TryGetTransform(out TransformState transform)
        {
            if (!IsAlive)
            {
                transform = TransformState.Identity;
                return false;
            }

            transform = RobotEditTransform.FromUnity(root.transform);
            return true;
        }
    }

    internal static class RobotEditTransform
    {
        public static TransformState FromUnity(Transform transform)
        {
            var position = transform.position;
            var rotation = transform.rotation;
            var scale = transform.localScale;
            return new TransformState(
                new Vec3(position.x, position.y, position.z),
                new Quat(rotation.x, rotation.y, rotation.z, rotation.w),
                new Vec3(scale.x, scale.y, scale.z));
        }

        public static void Apply(Transform transform, TransformState state)
        {
            transform.SetPositionAndRotation(
                new Vector3(state.Position.X, state.Position.Y, state.Position.Z),
                new Quaternion(state.Rotation.X, state.Rotation.Y, state.Rotation.Z, state.Rotation.W));
            transform.localScale = new Vector3(state.Scale.X, state.Scale.Y, state.Scale.Z);
        }
    }
}
